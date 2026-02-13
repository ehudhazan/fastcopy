using System.IO.Compression;
using Grpc.Core;
using ZstdSharp;
using Microsoft.Extensions.Logging;

namespace FastCopy.Services;

public class GrpcFileTransferService : FastCopy.TransferService.TransferServiceBase
{
    private readonly ZeroGCProvider _zeroGcProvider;
    private readonly ILogger<GrpcFileTransferService> _logger;

    public GrpcFileTransferService(ILogger<GrpcFileTransferService> logger) 
    {
        _zeroGcProvider = new ZeroGCProvider();
        _logger = logger;
    }

    public override async Task StreamFiles(IAsyncStreamReader<FileChunk> requestStream, IServerStreamWriter<TransferStatus> responseStream, ServerCallContext context)
    {
        try 
        {
            if (!await requestStream.MoveNext()) return;
            
            var firstChunk = requestStream.Current;
            var filePath = firstChunk.FilePath;
            
            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogWarning("No file path provided in the first chunk.");
                // We could throw here, but let's just return for now or maybe log and exit.
                throw new RpcException(new Status(StatusCode.InvalidArgument, "File path not provided"));
            }

            // Ensure directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var streamAdapter = new GrpcChunkStream(requestStream, firstChunk);
            Stream input = streamAdapter;
            
            if (firstChunk.IsCompressed)
            {
                input = new DecompressionStream(streamAdapter);
            }

            using var destination = new FileStream(filePath, new FileStreamOptions 
            { 
                Mode = FileMode.Create, 
                Access = FileAccess.Write, 
                Share = FileShare.None,
                PreallocationSize = 0 // Optional
            });
            
            long bytesReceived = 0;
            var cancellationToken = context.CancellationToken;
            
            // Start a background task to report progress periodically
            _ = Task.Run(async () => 
            {
                long lastReported = -1;
                while (!cancellationToken.IsCancellationRequested)
                {
                    long current = Interlocked.Read(ref bytesReceived);
                    if (current != lastReported)
                    {
                        try
                        {
                            await responseStream.WriteAsync(new TransferStatus { BytesReceived = current });
                            lastReported = current;
                        }
                        catch
                        {
                            // Stream might be closed or cancelled
                            break;
                        }
                    }
                    
                    try 
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, cancellationToken);

            await _zeroGcProvider.CopyStreamAsync(
                input, 
                destination, 
                (ref CopyProgress p) => 
                {
                    Interlocked.Exchange(ref bytesReceived, p.TotalBytesCopied);
                }, 
                cancellationToken
            );
            
            // Final update
            await responseStream.WriteAsync(new TransferStatus { BytesReceived = bytesReceived });
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            _logger.LogError(ex, "Error during file transfer");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
