using Google.Apis.PhotosLibrary.v1;
using Google.Apis.PhotosLibrary.v1.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using google_photos_upload.Extensions;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.MetaData.Profiles.Exif;
using System.Text.Json;

namespace google_photos_upload.Model
{
    class MyImage
    {
        private const string PhotosLibraryPasebath = "https://photoslibrary.googleapis.com/v1/uploads";
        private readonly ILogger _logger = null;
        private readonly PhotosLibraryService service = null;

        private readonly FileInfo mediaFile = null;

        /// <summary>
        /// Google Photos UploadToken for the file when it has been uploaded
        /// </summary>
        public string UploadToken { get; internal set; } = null;

        //Get from config file if we should upload img without EXIF
        private readonly bool conf_IMG_UPLOAD_NO_EXIF = Convert.ToBoolean(System.Configuration.ConfigurationManager.AppSettings["IMG_UPLOAD_NO_EXIF"]);



        /// <summary>
        /// Supported file formats
        /// </summary>
        private static readonly string[] allowedMovieFormats = { "mov", "avi", "mp4" };
        private static readonly string[] allowedPhotoFormats = { "jpg", "jpeg", "gif", "cr2", "3gp", "png", "vob" };
        private static readonly string[] ignoreFiletypes = { "txt", "thm", "json" };



        public MyImage(ILogger logger, PhotosLibraryService photoService, FileInfo imgFile)
        {
            this._logger = logger;
            this.service = photoService;
            this.mediaFile = imgFile;
            this.UploadStatus = UploadStatus.NotStarted;
            this.ImageMediaType = GetMediaType();
        }

        public MediaType ImageMediaType { get; private set; }


        public UploadStatus UploadStatus
        {
            get;
            set;
        }

        private string NameNoFileExtension
        {
            get { return Path.GetFileNameWithoutExtension(Name); }
        }

        /// <summary>
        /// Photo File Name
        /// </summary>
        public string Name
        {
            get { return mediaFile.Name; }
        }

        /// <summary>
        /// Image name in ASCII format
        /// </summary>
        private string NameASCII
        {
            get { return Name.UnicodeToASCII(); }
        }

        public string Description
        {
            get
            {
                try
                {
                    var imageInfo = Image.Identify(this.GetFileStream());
                    ExifValue exifValue = imageInfo?.MetaData?.ExifProfile?.GetValue(SixLabors.ImageSharp.MetaData.Profiles.Exif.ExifTag.ImageDescription);

                    if (exifValue != null)
                        return exifValue.ToString();
                }
                catch (Exception)
                {
                    //Failed to read Description from EXIF data
                    _logger.LogWarning("Failed reading Description EXIF data...");
                }

                //Use filename without extension, if EXIF tag is not there or for other file types like Movies
                return NameNoFileExtension;
            }
        }

        public bool IsPhoto
        {
            get
            {
                return ImageMediaType == MediaType.Photo;
            }
        }


        public bool IsMovie
        {
            get
            {
                return ImageMediaType == MediaType.Movie;
            }
        }


        /// <summary>
        /// Gets the 'Date Taken Original' value in DateTime format, derived from image EXIF data (avoids loading the whole image file into memory).
        /// </summary>
        private bool HasDateImageWasTaken
        {
            get
            {
                try
                {
                    var imageInfo = Image.Identify(this.GetFileStream());
                    ExifValue exifValue = imageInfo?.MetaData?.ExifProfile?.GetValue(SixLabors.ImageSharp.MetaData.Profiles.Exif.ExifTag.DateTimeOriginal);

                    string datetimeOriginaltxt = exifValue?.Value?.ToString();

                    if (string.IsNullOrEmpty(datetimeOriginaltxt))
                        return false;

                    return true;
                }
                catch (Exception)
                {
                    _logger.LogWarning("Failed reading Image Date EXIF data...");
                }

                //Unable to extract EXIF data from file
                return false;
            }
        }


        /// <summary>
        /// Get the media type for the file set on the mediaFile class variable.
        /// </summary>
        /// <returns>Media Type</returns>
        private MediaType GetMediaType()
        {
            string filename = mediaFile.Name.ToLower();
            string fileext = Path.GetExtension(filename).ToLower();

            //Is Movie?
            if (ignoreFiletypes.Any(fileext.Contains))
            {
                return MediaType.Ignore;
            }
            else if (allowedMovieFormats.Any(fileext.Contains))
            {
                return MediaType.Movie;
            }
            else if (allowedPhotoFormats.Any(fileext.Contains))
            {
                return MediaType.Photo;
            }

            return MediaType.Unknown;
        }


        public bool IsFormatSupported
        {
            get
            {
                try
                {
                    if (IsPhoto)
                    {
                        // EXIF property "Date Taken Original" must be set on the image file to ensure correct date in Google Photos
                        if (!HasDateImageWasTaken)
                        {
                            if (!conf_IMG_UPLOAD_NO_EXIF)
                                return false;

                            _logger.LogWarning("Image will appear with today's date in Google Photos as EXIF data is missing");
                        }


                    }
                    else if (IsMovie)
                    {
                        //Do check or Movie
                    }
                    else
                    {
                        //The file is not a supported Photo or Movie format
                        _logger.LogWarning("The file type is not supported");
                        return false;
                    }

                }
                catch (Exception)
                {
                    //What to do?
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Perform upload of Photo file to Google Cloud
        /// </summary>
        /// <returns></returns>
        public bool UploadMedia()
        {
            UploadStatus = UploadStatus.UploadInProgress;

            this.UploadToken = UploadMediaFile(service);

            if (UploadToken is null)
            {
                UploadStatus = UploadStatus.UploadNotSuccessfull;
            
                return false;
            }

            //UploadStatus will be set to 'UploadSuccess' later after it has been added to Album

            return true;
        }

        public static void ListImages(string album_id, PhotosLibraryService service, ILogger logger)
        {
            logger.LogInformation("");
            logger.LogInformation("Fetching media info for albun " + album_id);

            SearchMediaItemsResponse response = null;
            try
            {
                MediaItemsResource.SearchRequest request = service.MediaItems.Search(new SearchMediaItemsRequest
                {
                    AlbumId = album_id
                });

                response = request.Execute();
            }
            catch (Exception e)
            {
                logger.LogError("error getting media", e);
            }

            if (response.MediaItems.Any())
            {
                logger.LogInformation("media info:");

                foreach (var item in response.MediaItems)
                {
                    logger.LogInformation($"> {JsonSerializer.Serialize(item)}");
                }
            }
            else
            { logger.LogInformation("No media found."); }
        }

        public static void GetMediaInfo(string mediaItemId, PhotosLibraryService service, ILogger logger)
        {
            logger.LogInformation("");
            logger.LogInformation("Fetching media info for " + mediaItemId);


            var request = service.MediaItems.Get(mediaItemId);

            var response = request.Execute();
            logger.LogInformation("media info:");
            if (response != null)
            {
                logger.LogInformation($"> { JsonSerializer.Serialize(response)}");
            }
            else
            { logger.LogInformation("No media found."); }
        }

        /// <summary>
        /// Upload photo to Google Photos.
        /// API guide for media upload:
        /// https://developers.google.com/photos/library/guides/upload-media
        /// 
        /// The implementation is inspired by this post due to limitations in the Google Photos API missing a method for content upload,
        /// but instead of System.Net.HttpClient it has been rewritten to use Google Photo's HTTP Client as it the  has the Bearer Token already handled.
        /// https://stackoverflow.com/questions/51576778/upload-photos-to-google-photos-api-error-500
        /// </summary>
        /// <param name="photo"></param>
        /// <returns>UploadToken from Google Photos. Null is returned if upload was not succesful.</returns>
        private string UploadMediaFile(PhotosLibraryService photoService)
        {
            string newUploadToken = null;

            try
            {
                var fileStream = this.GetFileStream();

                //Create byte array to store the image for transfer
                byte[] pixels = new byte[fileStream.Length];

                //Read image into the pixels byte array
                fileStream.Read(pixels, 0, (int)fileStream.Length);

                //Set http headers per Google Photos API requirement
                //https://developers.google.com/photos/library/guides/upload-media
                var httpContent = new ByteArrayContent(pixels);
                httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                httpContent.Headers.Add("X-Goog-Upload-File-Name", NameASCII);
                httpContent.Headers.Add("X-Goog-Upload-Protocol", "raw");

                //Object to store response
                HttpResponseMessage mediaResponse = null;

                try
                {
                    //Send HTTP Post request
                    mediaResponse = photoService.HttpClient.PostAsync(PhotosLibraryPasebath, httpContent).Result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception occured during file posting. Inner Exception: {0}", ex.InnerException.ToString());
                }

                if (mediaResponse is null)
                    _logger.LogDebug("mediaResponse is null");
                else if (mediaResponse.IsSuccessStatusCode)
                    newUploadToken = mediaResponse.Content.ReadAsStringAsync().Result;
                else
                    _logger.LogWarning("Upload Media Response. Status Code: {0}. Reason Phrase: {1}", mediaResponse.StatusCode, mediaResponse.ReasonPhrase);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Upload of Image stream failed");
                throw;
            }

            //Returning the new UploadToken, or if none will return null.
            return newUploadToken;
        }

        internal void DeleteFile()
        {
            this.mediaFile.Delete();
        }

        internal void CloseFileStream()
        {
            try {
                this._fileStream?.Close();
                this._fileStream?.Dispose();
                this._fileStream = null;
            }
            catch (Exception)
            {
                //do nothing
            }
        }

        private FileStream _fileStream;
        public  FileStream GetFileStream()
        {
            if (_fileStream is null || !_fileStream.CanRead)
                _fileStream = mediaFile.OpenRead();
            return _fileStream;
            //TODO: close the file stream somewhere.
        }
    }
}
