﻿using System.Net;
using VidSync.Services;
using Windows.UI.Popups;

namespace VidSync.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    private int maxConcurrentDownloads = 2;
    private SemaphoreSlim downloadSemaphore = new SemaphoreSlim(2);

    public MainViewModel()
    {
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        await LoadDownloadListAsync();
        CheckDownloads();
        GetCookies();

    }

    private void GetCookies()
    {
        try
        {
            // Load cookies
            List<Cookie> storedCookies = CookieManager.LoadCookies();

            // Use the cookies (e.g., set them in the WebView2 control)
            Cookies = storedCookies;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    public async Task AnalyzeVideoLinkAsync()
    {
        if (string.IsNullOrEmpty(VideoLink)) return;
        if (IsAnalyzing) return;

        try
        {
            Qualities.Clear();
            IsAnalyzing = true;
            IsAnalyzed = false;

            var bitmap = new BitmapImage();

            var youtube = new YoutubeClient(Cookies);
            var video = await youtube.Videos.GetAsync(VideoLink);
            var streamManifest = await youtube.Videos.Streams.GetManifestAsync(VideoLink);

            // Get highest quality muxed stream
            StreamInfo = streamManifest.GetMuxedStreams();

            foreach (var item in StreamInfo.ToList())
            {
                Qualities.Add(item.VideoQuality.Label);
            }


            ThumbnailUrl = video.Thumbnails.LastOrDefault()!.Url;

            bitmap.UriSource = new Uri(ThumbnailUrl);

            VideoId = video.Id;
            VideoTitle = video.Title;
            VideoDuration = video.Duration!.Value.ToString();
            VideoThumbnail = bitmap;
            SelectedQuality = "720p";
            IsAnalyzed = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            IsAnalyzed = false;
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private async Task AddDownloadItem()
    {
        if (DownloadItems.Any(x => x.Id == VideoId))
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                App.MainWindow.ShowMessageDialogAsync("Download item already exist!!", "Vidsync");

                App.MainWindow.BringToFront();
            });

            return;
        }

        try
        {
            var videoPlayerStream = StreamInfo.First(video => video.VideoQuality.Label == SelectedQuality);

            DownloadItem downloadItem = new DownloadItem()
            {
                Id = VideoId,
                Title = VideoTitle,
                Duration = VideoDuration,
                ImageUrl = ThumbnailUrl,
                AudioCodec = videoPlayerStream.AudioCodec,
                FileFormat = videoPlayerStream.Container.Name,
                FileSize = $"{Math.Round(videoPlayerStream.Size.MegaBytes, 1)}MB",
                VideoCodec = videoPlayerStream.VideoCodec,
                VideoInfo = $"{videoPlayerStream.VideoResolution.Width}x{videoPlayerStream.VideoResolution.Height} {videoPlayerStream.VideoQuality.Framerate}fps",
                RemoteUrl = videoPlayerStream.Url,
                LocalPath = LocalPath,
                CreatedAt = DateTime.Now,
                CancellationTokenSource = new CancellationTokenSource(),
            };

            DownloadItems.Add(downloadItem);

            SaveDownloadList();

            await StartDownloadAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private void CheckDownloads()
    {
        try
        {
            foreach (var item in DownloadItems)
            {
                if (item.ProgressText == "Completed" || item.ProgressText == "100%")
                {
                    item.IsError = false;
                    item.IsDownloading = false;
                    item.IsCompleted = true;
                    item.ProgressText = "Completed";
                }
                else
                {
                    item.IsError = true;
                    item.IsDownloading = false;
                    item.IsCompleted = false;
                    item.ProgressText = "Retry";
                }

            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        try
        {
            //Use SemaphoreSlim to limit concurrent downloads
            await downloadSemaphore.WaitAsync();

            var downloadTasks = DownloadItems
            .Where(item => !item.IsCompleted && !item.IsDownloading)
            .Select(item => StartDownloadItemAsync(item));

            await Task.WhenAll(downloadTasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        finally
        {
            // Release the semaphore when all downloads are complete or an exception occurs
            downloadSemaphore.Release();
        }
    }

    private async Task StartDownloadItemAsync(DownloadItem item)
    {
        try
        {
            item.IsError = false;
            item.IsDownloading = true;
            item.ProgressText = "Downloading...";
            item.CancellationTokenSource = new CancellationTokenSource();

            await DownloadItemAsync(item, item.CancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }


    [RelayCommand]
    private void StopDownload(DownloadItem item)
    {
        try
        {
            if (item.IsDownloading)
            {
                // Cancel the download operation associated with the DownloadItem
                item.CancellationTokenSource?.Cancel();

                // Update the item's properties to reflect the cancellation
                item.IsDownloading = false;
                item.IsCompleted = false;
                item.IsError = true;
                item.ProgressText = "Cancelled";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private async Task DownloadItemAsync(DownloadItem download, CancellationToken cancellationToken)
    {
        try
        {
            ProgressChanged = 0;

            var downloadFileUrl = download.RemoteUrl;

            var saveFileName = PathExtension.ConvertToValidFileName(download.Title);

            var destinationFilePath = $"{download.LocalPath}\\{saveFileName}.mp4";

            using (var client = new DownloadService(downloadFileUrl, destinationFilePath))
            {
                client.CancellationToken = cancellationToken;

                client.ProgressChanged += (totalFileSize, totalBytesDownloaded, progressPercentage) =>
                {
                    var dispatcherQueue = App.MainWindow.DispatcherQueue;

                    if (dispatcherQueue != null)
                    {
                        dispatcherQueue.TryEnqueue(() =>
                        {
                            Debug.WriteLine($"{progressPercentage}% ({totalBytesDownloaded}/{totalFileSize})");
                            download.ProgressText = $"{progressPercentage}%";
                            download.Progress = (double)progressPercentage;
                            ProgressChanged = Math.Round((double)progressPercentage / 100, 2);
                        });
                    }

                    // Check for cancellation and stop the download if necessary
                    if (download.CancellationTokenSource.Token.IsCancellationRequested)
                    {
                        //The cancellation will be handled by exceptions
                    }
                };

                try
                {
                    // Pass CancellationToken to the StartDownload method
                    await client.StartDownload(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Handle the cancellation exception
                    Debug.WriteLine("Download Cancelled");
                    return;
                }
            }

            await Task.CompletedTask;

            download.IsDownloading = false;
            download.IsCompleted = true;
            download.IsError = false;
            download.ProgressText = "Completed";
            SaveDownloadList();
            App.GetService<IAppNotificationService>().Show(string.Format("AppNotificationDownloadComplete".GetLocalized(), AppContext.BaseDirectory));
        }
        catch (Exception ex)
        {
            download.IsDownloading = false;
            download.IsCompleted = false;
            download.IsError = download.CancellationTokenSource.Token.IsCancellationRequested; // Set IsError based on cancellation
            SaveDownloadList();
            Debug.WriteLine(ex.Message);
            App.GetService<IAppNotificationService>().Show(string.Format("AppNotificationDownloadError".GetLocalized(), AppContext.BaseDirectory));
        }
    }

    [RelayCommand]
    private void DeleteItem(DownloadItem item)
    {
        try
        {
            DownloadItems.Remove(item);
            SaveDownloadList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    //[RelayCommand]
    //private async Task PauseDownload(string id)
    //{
    //    var downloadItem = GetDownloadItem(id);
    //    if (downloadItem != null)
    //    {
    //        //downloadItem.IsPaused = true;
    //        downloadItem.CancellationTokenSource.Cancel();
    //    }
    //}

    //[RelayCommand]
    //async Task ResumeDownload(string id)
    //{
    //    var downloadItem = GetDownloadItem(id);
    //    if (downloadItem != null)
    //    {
    //        //downloadItem.IsPaused = false;
    //        downloadItem.CancellationTokenSource = new CancellationTokenSource();
    //        //await StartDownloadAsync(downloadItem);
    //    }
    //}

    [RelayCommand]
    private void GotoSettingsPage()
    {
        NavigationService.NavigateTo(typeof(SettingsViewModel).FullName!.ToString(), null);
    }

    [RelayCommand]
    private async void GotoLoginPage()
    {
        if(Cookies.Count < 0)
        {
            NavigationService.NavigateTo(typeof(LoginViewModel).FullName!.ToString(), null);
        }
        else
        {
            Console.WriteLine("Already logged in");
            //MessageDialog dialog = new MessageDialog("Already logged in", "Login");
            //await dialog.ShowAsync();
        }
    }

    private DownloadItem? GetDownloadItem(string id)
    {
        return DownloadItems.Where(item => item.Id == id).FirstOrDefault();
    }


    private void SaveDownloadList()
    {
        try
        {
            var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vidsync.json");
            var jsonString = System.Text.Json.JsonSerializer.Serialize(DownloadItems);
            File.WriteAllText(filePath, jsonString);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private async Task LoadDownloadListAsync()
    {
        try
        {
            var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vidsync.json");

            if (File.Exists(filePath))
            {
                var jsonString = await File.ReadAllTextAsync(filePath);
                DownloadItems = System.Text.Json.JsonSerializer.Deserialize<ObservableCollection<DownloadItem>>(jsonString);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    public ObservableCollection<DownloadItem> DownloadItems { get; set; } = new ObservableCollection<DownloadItem>();
    public ObservableCollection<string> Qualities { get; set; } = new ObservableCollection<string>();

    [ObservableProperty]
    private string videoId;

    [ObservableProperty]
    private DownloadItem selectedItem;

    [ObservableProperty]
    private string videoLink = string.Empty;

    [ObservableProperty]
    private string videoTitle = string.Empty;

    [ObservableProperty]
    private ImageSource videoThumbnail = null!;

    [ObservableProperty]
    private string thumbnailUrl = null!;

    [ObservableProperty]
    private string videoDuration = string.Empty;

    [ObservableProperty]
    private string localPath = Environment.GetEnvironmentVariable("USERPROFILE") + @"\" + @"Downloads\";

    [ObservableProperty]
    private IEnumerable<MuxedStreamInfo>? streamInfo;

    [ObservableProperty]
    private string selectedQuality;

    [ObservableProperty]
    private double progressChanged;

}
