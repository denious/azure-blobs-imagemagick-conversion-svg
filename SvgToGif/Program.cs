using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Extensions.Configuration;

namespace SvgToGif
{
    class Program
    {
        private const string ContainerName = "chromatograms";
        private const int MaxBlobsResults = 100;
        private const int MaxThreads = 4;

        static void Main(string[] args)
        {
            // get secrets
            var builder = new ConfigurationBuilder();
            builder.AddUserSecrets<Program>();
            var configuration = builder.Build();

            // connect to blob storage
            var connectionString = configuration["ConnectionString"];

            // connect to container
            var storageAccount = CloudStorageAccount.Parse(connectionString);
            var cloudBlobClient = storageAccount.CreateCloudBlobClient();
            var cloudBlobContainer = cloudBlobClient.GetContainerReference(ContainerName);

            // start going through X documents at a time
            BlobContinuationToken blobContinuationToken = null;
            do
            {
                // fetch
                const string blobPrefix = "";
                var results = cloudBlobContainer.ListBlobsSegmented(
                    blobPrefix,
                    true,
                    BlobListingDetails.None,
                    MaxBlobsResults,
                    blobContinuationToken,
                    new BlobRequestOptions(),
                    null);

                // refresh continuation token
                blobContinuationToken = results.ContinuationToken;

                var semaphore = new SemaphoreSlim(MaxThreads);

                //foreach (var svgBlob in results.Results
                var blobTasks = results.Results
                    .OfType<CloudBlockBlob>()
                    .Where(blob => blob.Name.EndsWith(".svg"))
                    .Where(blob => blob.Properties.Length > 1024)
                    .Select(async svgBlob =>
                    {
                        try
                        {
                            await semaphore.WaitAsync();

                            // download svg
                            var svgStream = new MemoryStream();
                            await svgBlob.DownloadToStreamAsync(svgStream);

                            // reset stream position for Magick to access it
                            svgStream.Position = 0;

                            // convert name
                            var gifName = Path.ChangeExtension(svgBlob.Name, ".gif");

                            // convert to gif
                            MemoryStream gifStream;
                            using (var svgToGif = new MagickImage(svgStream, new MagickReadSettings
                            {
                                Density = new Density(1000)
                            }))
                            {
                                svgToGif.Trim();
                                svgToGif.RePage();

                                if (svgToGif.Width > 1600)
                                {
                                    var resizePercentage = 100 / ((double) svgToGif.Width / 1600);
                                    svgToGif.Resize(new Percentage(resizePercentage));
                                }

                                svgToGif.Format = MagickFormat.Gif;

                                var gifByteArray = svgToGif.ToByteArray();
                                gifStream = new MemoryStream(gifByteArray);

                                var outputPath = configuration["OutputPath"] + gifName;
                                await File.WriteAllBytesAsync(outputPath, gifByteArray);
                            }

                            // upload gif
                            Console.Write(".");

                            var gifBlob = cloudBlobContainer.GetBlockBlobReference(gifName);
                            //gifBlob.UploadFromStream(gifStream);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                            Console.WriteLine(e.StackTrace);
                        }

                        semaphore.Release();
                    }).ToArray();

                Task.WaitAll(blobTasks);

            } while (blobContinuationToken != null);

            Console.WriteLine();
            Console.WriteLine("Done! Press any key to exit.");
            Console.ReadKey();
        }
    }
}
