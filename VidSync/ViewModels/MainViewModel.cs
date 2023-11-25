﻿using AngleSharp.Io;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using VidSync.Contracts.Services;
using VidSync.Helpers;
using VidSync.Models;
using VidSync.Services;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace VidSync.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    private int maxConcurrentDownloads = 5;

    public MainViewModel()
    {
        LoadFromJson();
        StartDownloadAsync();
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

            var youtube = new YoutubeClient();
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

            VideoTitle = video.Title;
            VideoDuration = video.Duration!.Value.ToString();
            VideoThumbnail = bitmap;
            SelectedQuality = "720p";
            IsAnalyzed = true;
        }
        catch (Exception)
        {
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
        try
        {
            var videoPlayerStream = StreamInfo.First(video => video.VideoQuality.Label == SelectedQuality);

            DownloadItem downloadItem = new DownloadItem()
            {
                Id = Guid.NewGuid().ToString(),
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

            SaveToJson();

            await StartDownloadAsync();
        }
        catch (Exception)
        {
            throw;
        }
    }

    private async Task StartDownloadAsync()
    {
        try
        {
            foreach (var item in DownloadItems)
            {
                if (!item.IsCompleted && !item.IsDownloading)
                {
                    item.IsDownloading = true;

                    await DownloadItemAsync(item);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    private async Task DownloadItemAsync(DownloadItem download)
    {
        try
        {
            ProgressChanged = 0;

            var downloadFileUrl = download.RemoteUrl;

            var saveFileName = PathExtension.ConvertToValidFileName(download.Title);

            var destinationFilePath = $"{download.LocalPath}\\{saveFileName}.mp4";

            using (var client = new DownloadService(downloadFileUrl, destinationFilePath))
            {
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
                };

                await client.StartDownload();
            }

            await Task.CompletedTask;

            download.IsDownloading = false;
            download.IsCompleted = true;
            SaveToJson();
            App.GetService<IAppNotificationService>().Show(string.Format("AppNotificationSamplePayload".GetLocalized(), AppContext.BaseDirectory));
        }
        catch (Exception ex)
        {
            download.IsDownloading = false;
            Debug.WriteLine(ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteItem(DownloadItem item)
    {
        try
        {
            DownloadItems.Remove(item);
            SaveToJson();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    [RelayCommand]
    private async Task PauseDownload(string id)
    {
        var downloadItem = GetDownloadItem(id);
        if (downloadItem != null)
        {
            downloadItem.IsPaused = true;
            downloadItem.CancellationTokenSource.Cancel();
        }
    }

    [RelayCommand]
    async Task ResumeDownload(string id)
    {
        var downloadItem = GetDownloadItem(id);
        if (downloadItem != null)
        {
            downloadItem.IsPaused = false;
            downloadItem.CancellationTokenSource = new CancellationTokenSource();
            //await StartDownloadAsync(downloadItem);
        }
    }

    [RelayCommand]
    private void GotoSettingsPage()
    {
        NavigationService.NavigateTo(typeof(SettingsViewModel).FullName!.ToString(), null);
    }

    private DownloadItem? GetDownloadItem(string id)
    {
        return DownloadItems.Where(item => item.Id == id).FirstOrDefault();
    }


    private void SaveToJson()
    {
        try
        {
            var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"vidsync.json");
            var jsonString = JsonSerializer.Serialize(DownloadItems);
            File.WriteAllText(filePath, jsonString);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private void LoadFromJson()
    {
        try
        {
            var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vidsync.json");

            if (File.Exists(filePath))
            {
                var jsonString = File.ReadAllText(filePath);
                DownloadItems = JsonSerializer.Deserialize<ObservableCollection<DownloadItem>>(jsonString);
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