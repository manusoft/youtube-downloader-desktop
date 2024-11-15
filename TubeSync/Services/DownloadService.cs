﻿namespace TubeSync.Services;

public class DownloadService : IDisposable
{
    private readonly string _downloadUrl;
    private readonly string _destinationFilePath;

    private HttpClient httpClient;

    public delegate void ProgressChangedHandler(long? totalFileSize, long totalBytesDownloaded, double? progressPercentage);

    public event ProgressChangedHandler ProgressChanged;

    public CancellationToken CancellationToken { get; set; }

    public DownloadService(string downloadUrl, string destinationFilePath)
    {
        _downloadUrl = downloadUrl;
        _destinationFilePath = destinationFilePath;
    }

    public async Task StartDownload(CancellationToken cancellationToken)
    {
        httpClient = new HttpClient { Timeout = TimeSpan.FromDays(1) };

        using (var response = await httpClient.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            // Check for cancellation before processing the response
            if (CancellationToken.IsCancellationRequested)
            {
                // Handle cancellation, e.g., by throwing an exception
                throw new OperationCanceledException(cancellationToken);
            }

            await DownloadFileFromHttpResponseMessage(response, cancellationToken);
        }

    }

    private async Task DownloadFileFromHttpResponseMessage(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        using (var contentStream = await response.Content.ReadAsStreamAsync())
            await ProcessContentStream(totalBytes, contentStream, cancellationToken);
    }

    private async Task ProcessContentStream(long? totalDownloadSize, Stream contentStream, CancellationToken cancellationToken)
    {
        var totalBytesRead = 0L;
        var readCount = 0L;
        var buffer = new byte[8192];
        var isMoreToRead = true;

        using (var fileStream = new FileStream(_destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
        {
            do
            {
                var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    isMoreToRead = false;
                    TriggerProgressChanged(totalDownloadSize, totalBytesRead);
                    continue;
                }

                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                totalBytesRead += bytesRead;
                readCount += 1;

                if (readCount % 100 == 0)
                    TriggerProgressChanged(totalDownloadSize, totalBytesRead);
            }
            while (isMoreToRead);
        }
    }

    private void TriggerProgressChanged(long? totalDownloadSize, long totalBytesRead)
    {
        if (ProgressChanged == null)
            return;

        double? progressPercentage = null;
        if (totalDownloadSize.HasValue)
            progressPercentage = Math.Round((double)totalBytesRead / totalDownloadSize.Value * 100, 2);

        ProgressChanged(totalDownloadSize, totalBytesRead, progressPercentage);
    }

    public void Dispose()
    {
        httpClient?.Dispose();
    }
}