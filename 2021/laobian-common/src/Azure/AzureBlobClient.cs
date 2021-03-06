﻿using System;
using Laobian.Common.Base;
using Laobian.Common.Config;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Laobian.Common.Azure
{
    /// <summary>
    /// Implementation of <see cref="IAzureBlobClient"/>
    /// </summary>
    public class AzureBlobClient : IAzureBlobClient
    {
        private readonly ConcurrentDictionary<string, bool> _containerCheckingDict;

        /// <summary>
        /// Constructor of <see cref="AzureBlobClient"/>
        /// </summary>
        public AzureBlobClient()
        {
            _containerCheckingDict = new ConcurrentDictionary<string, bool>();
        }

        #region Implemention of IAzureBlobClient

        /// <inheritdoc />
        public async Task<T> DownloadAsync<T>(BlobContainer container, string blobName, BlobType blobType)
        {
            var containerRef = await GetContainerReferenceAsync(container);
            var blob = containerRef.GetBlockBlobReference(PrivateBlobResolver.Normalize(blobName));

            if (!await blob.ExistsAsync())
            {
                return default; // we don't want to serializer throws exception later
            }

            if (blobType == BlobType.Json)
            {
                var str = await blob.DownloadTextAsync();
                return SerializeHelper.FromJson<T>(str);
            }

            if (blobType == BlobType.ProtoBuf)
            {
                using (var ms = new MemoryStream())
                {
                    await blob.DownloadToStreamAsync(ms);
                    return SerializeHelper.FromProtoBuf<T>(ms);
                }
            }

            throw new NotSupportedException();
        }


        /// <inheritdoc />
        public async Task<bool> ExistAsync(BlobContainer container, string blobName)
        {
            var containerRef = await GetContainerReferenceAsync(container);
            var blob = containerRef.GetBlockBlobReference(PrivateBlobResolver.Normalize(blobName));
            return await blob.ExistsAsync();
        }

        /// <inheritdoc />
        public async Task<IEnumerable<BlobData>> ListAsync(BlobContainer container, string prefix = null)
        {
            var containerRef = await GetContainerReferenceAsync(container);
            BlobContinuationToken blobContinuationToken = null;

            var result = new List<BlobData>();

            do
            {
                var results = await containerRef.ListBlobsSegmentedAsync(
                    prefix,
                    true,
                    BlobListingDetails.All,
                    null,
                    blobContinuationToken,
                    null,
                    null);
                // Get the value of the continuation token returned by the listing call.
                blobContinuationToken = results.ContinuationToken;
                foreach (var item in results.Results.Cast<CloudBlockBlob>())
                {
                    var ms = new MemoryStream();
                    await item.DownloadToStreamAsync(ms);
                    ms.Seek(0, SeekOrigin.Begin);

                    result.Add(new BlobData
                    {
                        BlobName = item.Name,
                        PrimaryUri = item.StorageUri.PrimaryUri.AbsolutePath,
                        Stream = ms,
                        Size = item.Properties.Length,
                        Created = item.Properties.Created,
                        ContentMd5 = item.Properties.ContentMD5
                    });
                }
            } while (blobContinuationToken != null);

            return result;
        }

        /// <inheritdoc />
        public async Task<string> UploadAsync<T>(BlobContainer container, string blobName, BlobType blobType, T obj)
        {
            var containerRef = await GetContainerReferenceAsync(container);
            var blob = containerRef.GetBlockBlobReference(PrivateBlobResolver.Normalize(blobName));

            if (blobType == BlobType.Json && obj is string str)
            {
                await blob.UploadTextAsync(str);
            }
            else if (blobType == BlobType.Other && obj is Stream objStream)
            {
                using (objStream)
                {
                    objStream.Seek(0, SeekOrigin.Begin);
                    await blob.UploadFromStreamAsync(objStream);
                }
            }
            else if (blobType == BlobType.ProtoBuf)
            {
                using (var stream = SerializeHelper.ToProtoBuf(obj))
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    await blob.UploadFromStreamAsync(stream);
                }
            }
            else
            {
                throw new NotSupportedException();
            }

            blob.Properties.CacheControl = "max-age=360000"; // for public resource, cache can help performance
            await blob.SetPropertiesAsync();
            return blob.StorageUri.PrimaryUri.AbsoluteUri;
        }

        #endregion

        private async Task<CloudBlobContainer> GetContainerReferenceAsync(BlobContainer containerName)
        {
            var container = PrivateBlobResolver.Normalize(containerName);
            var storageAccount = CloudStorageAccount.Parse(AppConfig.Default.AzureStorageConnection);
            var client = storageAccount.CreateCloudBlobClient();
            var containerRef = client.GetContainerReference(container);
            await EnsureContainerExistsAsync(containerRef);
            return containerRef;
        }

        private async Task EnsureContainerExistsAsync(CloudBlobContainer container)
        {
            var containerChecked = _containerCheckingDict.GetOrAdd(container.Name, false);
            if (!containerChecked)
            {
                await CreateContainerAsync(container);
                _containerCheckingDict.TryUpdate(container.Name, true, false);
            }
        }

        private async Task CreateContainerAsync(CloudBlobContainer containerRef)
        {
            await containerRef.CreateIfNotExistsAsync();

            var permissions = new BlobContainerPermissions
            {
                PublicAccess = containerRef.Name == PrivateBlobResolver.Normalize(BlobContainer.Public) ? BlobContainerPublicAccessType.Container : BlobContainerPublicAccessType.Off
            };

            await containerRef.SetPermissionsAsync(permissions);
        }
    }
}
