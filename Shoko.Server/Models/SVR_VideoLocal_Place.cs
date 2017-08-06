﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nancy.Extensions;
using NHibernate;
using NLog;
using NutzCode.CloudFileSystem;
using NutzCode.CloudFileSystem.Plugins.LocalFileSystem;
using Pri.LongPath;
using Shoko.Models.Azure;
using Shoko.Models.PlexAndKodi;
using Shoko.Models.Server;
using Shoko.Server.Commands;
using Shoko.Server.Databases;
using Shoko.Server.Extensions;
using Shoko.Server.FileHelper.MediaInfo;
using Shoko.Server.FileHelper.Subtitles;
using Shoko.Server.PlexAndKodi;
using Shoko.Server.Providers.Azure;
using Shoko.Server.Repositories;
using Shoko.Server.Repositories.Cached;

namespace Shoko.Server.Models
{
    public enum DELAY_IN_USE
    {
        FIRST = 750,
        SECOND = 3000,
        THIRD = 5000
    }

    public class SVR_VideoLocal_Place : VideoLocal_Place
    {
        internal SVR_ImportFolder ImportFolder => RepoFactory.ImportFolder.GetByID(ImportFolderID);

        public string FullServerPath
        {
            get
            {
                if (string.IsNullOrEmpty(ImportFolder?.ImportFolderLocation) || string.IsNullOrEmpty(FilePath))
                    return null;
                return Path.Combine(ImportFolder.ImportFolderLocation, FilePath);
            }
        }

        public SVR_VideoLocal VideoLocal => RepoFactory.VideoLocal.GetByID(VideoLocalID);

        private static Logger logger = LogManager.GetCurrentClassLogger();

        // returns false if we should try again after the timer
        private bool RenameFile()
        {
            var renamer = RenameFileHelper.GetRenamer();
            if (renamer == null) return true;
            string renamed = renamer.GetFileName(this);
            if (string.IsNullOrEmpty(renamed))
            {
                logger.Error("Error: The renamer returned a null or empty name for: " + FilePath);
                return true;
            }

            if (renamed.StartsWith("*Error: "))
            {
                logger.Error("Error: The renamer returned an error on file: " + FilePath + "\n            " + renamed);
                return true;
            }

            IFileSystem filesys = ImportFolder?.FileSystem;
            if (filesys == null)
                return true;
            // actually rename the file
            string fullFileName = FullServerPath;

            // check if the file exists

            FileSystemResult<IObject> re = filesys.Resolve(fullFileName);
            if ((re == null) || (!re.IsOk))
            {
                logger.Error("Error could not find the original file for renaming: " + fullFileName);
                return false;
            }
            IObject file = re.Result;
            // actually rename the file
            string path = Path.GetDirectoryName(fullFileName);
            string newFullName = (path == null ? null : Path.Combine(path, renamed));

            try
            {
                logger.Info($"Renaming file From ({fullFileName}) to ({newFullName})....");

                if (fullFileName.Equals(newFullName, StringComparison.InvariantCultureIgnoreCase))
                {
                    logger.Info($"Renaming file SKIPPED! no change From ({fullFileName}) to ({newFullName})");
                    return true;
                }

                FileSystemResult r = file?.FileSystem?.Resolve(newFullName);
                if (r != null && r.IsOk)
                {
                    logger.Info($"Renaming file SKIPPED! Destination Exists ({newFullName})");
                    return true;
                }

                r = file.Rename(renamed);
                if (r == null || !r.IsOk)
                {
                    logger.Info(
                        $"Renaming file FAILED! From ({fullFileName}) to ({newFullName}) - {r?.Error ?? "Result is null"}");
                    return false;
                }

                logger.Info($"Renaming file SUCCESS! From ({fullFileName}) to ({newFullName})");
                Tuple<SVR_ImportFolder, string> tup = VideoLocal_PlaceRepository.GetFromFullPath(newFullName);
                if (tup == null)
                {
                    logger.Error($"Unable to LOCATE file {newFullName} inside the import folders");
                    return false;
                }

                // Before we change all references, remap Duplicate Files
                List<DuplicateFile> dups = RepoFactory.DuplicateFile.GetByFilePathAndImportFolder(FilePath, ImportFolderID);
                if (dups != null && dups.Count > 0)
                {
                    foreach (var dup in dups)
                    {
                        bool dupchanged = false;
                        if (dup.FilePathFile1.Equals(FilePath, StringComparison.InvariantCultureIgnoreCase) &&
                            dup.ImportFolderIDFile1 == ImportFolderID)
                        {
                            dup.FilePathFile1 = tup.Item2;
                            dupchanged = true;
                        }
                        else if (dup.FilePathFile2.Equals(FilePath, StringComparison.InvariantCultureIgnoreCase) &&
                                 dup.ImportFolderIDFile2 == ImportFolderID)
                        {
                            dup.FilePathFile2 = tup.Item2;
                            dupchanged = true;
                        }
                        if (dupchanged) RepoFactory.DuplicateFile.Save(dup);
                    }
                }
                var filename_hash = RepoFactory.FileNameHash.GetByHash(VideoLocal.Hash);
                if (!filename_hash.Any(a => a.FileName.Equals(renamed)))
                {
                    FileNameHash fnhash = new FileNameHash
                    {
                        DateTimeUpdated = DateTime.Now,
                        FileName = renamed,
                        FileSize = VideoLocal.FileSize,
                        Hash = VideoLocal.Hash
                    };
                    RepoFactory.FileNameHash.Save(fnhash);
                }

                FilePath = tup.Item2;
                RepoFactory.VideoLocalPlace.Save(this);
            }
            catch (Exception ex)
            {
                logger.Info($"Renaming file FAILED! From ({fullFileName}) to ({newFullName}) - {ex.Message}");
                logger.Error(ex, ex.ToString());
            }
            return true;
        }

        public void RemoveRecord()
        {
            logger.Info("Removing VideoLocal_Place record for: {0}", FullServerPath ?? VideoLocal_Place_ID.ToString());
            List<SVR_AnimeEpisode> episodesToUpdate = new List<SVR_AnimeEpisode>();
            List<SVR_AnimeSeries> seriesToUpdate = new List<SVR_AnimeSeries>();
            SVR_VideoLocal v = VideoLocal;
            List<DuplicateFile> dupFiles =
                RepoFactory.DuplicateFile.GetByFilePathAndImportFolder(FilePath, ImportFolderID);

            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                if (v.Places.Count <= 1)
                {
                    episodesToUpdate.AddRange(v.GetAnimeEpisodes());
                    seriesToUpdate.AddRange(v.GetAnimeEpisodes().Select(a => a.GetAnimeSeries()));

                    using (var transaction = session.BeginTransaction())
                    {
                        RepoFactory.VideoLocalPlace.DeleteWithOpenTransaction(session, this);
                        RepoFactory.VideoLocal.DeleteWithOpenTransaction(session, v);
                        dupFiles?.ForEach(a => RepoFactory.DuplicateFile.DeleteWithOpenTransaction(session, a));
                        transaction.Commit();
                    }
                    CommandRequest_DeleteFileFromMyList cmdDel =
                        new CommandRequest_DeleteFileFromMyList(v.Hash, v.FileSize);
                    cmdDel.Save();
                }
                else
                {
                    using (var transaction = session.BeginTransaction())
                    {
                        RepoFactory.VideoLocalPlace.DeleteWithOpenTransaction(session, this);
                        dupFiles.ForEach(a => RepoFactory.DuplicateFile.DeleteWithOpenTransaction(session, a));
                        transaction.Commit();
                    }
                }
            }
            episodesToUpdate = episodesToUpdate.DistinctBy(a => a.AnimeEpisodeID).ToList();
            foreach (SVR_AnimeEpisode ep in episodesToUpdate)
            {
                try
                {
                    RepoFactory.AnimeEpisode.Save(ep);
                }
                catch (Exception ex)
                {
                    LogManager.GetCurrentClassLogger().Error(ex, ex.ToString());
                }
            }
            seriesToUpdate = seriesToUpdate.DistinctBy(a => a.AnimeSeriesID).ToList();
            foreach (SVR_AnimeSeries ser in seriesToUpdate)
            {
                ser.QueueUpdateStats();
            }
        }


        public void RemoveRecordWithOpenTransaction(ISession session, ICollection<SVR_AnimeEpisode> episodesToUpdate,
            ICollection<SVR_AnimeSeries> seriesToUpdate)
        {
            logger.Info("Removing VideoLocal_Place recoord for: {0}", FullServerPath ?? VideoLocal_Place_ID.ToString());
            SVR_VideoLocal v = VideoLocal;

            List<DuplicateFile> dupFiles =
                RepoFactory.DuplicateFile.GetByFilePathAndImportFolder(FilePath, ImportFolderID);

            if (v?.Places?.Count <= 1)
            {
                List<SVR_AnimeEpisode> eps = v?.GetAnimeEpisodes()?.Where(a => a != null).ToList();
                eps?.ForEach(episodesToUpdate.Add);
                eps?.Select(a => a.GetAnimeSeries()).ToList().ForEach(seriesToUpdate.Add);
                using (var transaction = session.BeginTransaction())
                {
                    RepoFactory.VideoLocalPlace.DeleteWithOpenTransaction(session, this);
                    RepoFactory.VideoLocal.DeleteWithOpenTransaction(session, v);
                    dupFiles.ForEach(a => RepoFactory.DuplicateFile.DeleteWithOpenTransaction(session, a));
                    transaction.Commit();
                }
                CommandRequest_DeleteFileFromMyList cmdDel =
                    new CommandRequest_DeleteFileFromMyList(v.Hash, v.FileSize);
                cmdDel.Save();
            }
            else
            {
                using (var transaction = session.BeginTransaction())
                {
                    RepoFactory.VideoLocalPlace.DeleteWithOpenTransaction(session, this);
                    dupFiles.ForEach(a => RepoFactory.DuplicateFile.DeleteWithOpenTransaction(session, a));
                    transaction.Commit();
                }
            }
        }

        public IFile GetFile()
        {
            IFileSystem fs = ImportFolder.FileSystem;
            FileSystemResult<IObject> fobj = fs?.Resolve(FullServerPath);
            if (fobj == null || !fobj.IsOk || fobj.Result is IDirectory)
                return null;
            return fobj.Result as IFile;
        }

        public static void FillVideoInfoFromMedia(SVR_VideoLocal info, Media m)
        {
            info.VideoResolution = !string.IsNullOrEmpty(m.Width) && !string.IsNullOrEmpty(m.Height)
                ? m.Width + "x" + m.Height
                : string.Empty;
            info.VideoCodec = !string.IsNullOrEmpty(m.VideoCodec) ? m.VideoCodec : m.Parts.SelectMany(a => a.Streams).FirstOrDefault(a => a.StreamType == "1")?.CodecID ?? string.Empty;
            info.AudioCodec = !string.IsNullOrEmpty(m.AudioCodec) ? m.AudioCodec : m.Parts.SelectMany(a => a.Streams).FirstOrDefault(a => a.StreamType == "2")?.CodecID ?? string.Empty;


            if (!string.IsNullOrEmpty(m.Duration))
            {
                bool isValidDuration = double.TryParse(m.Duration, out double duration);
                if (isValidDuration)
                    info.Duration =
                        (long) double.Parse(m.Duration, NumberStyles.Any, CultureInfo.InvariantCulture);
                else
                    info.Duration = 0;
            }
            else
                info.Duration = 0;

            info.VideoBitrate = info.VideoBitDepth = info.VideoFrameRate = info.AudioBitrate = string.Empty;
            List<Stream> vparts = m.Parts.SelectMany(a => a.Streams).Where(a => a.StreamType == "1").ToList();
            if (vparts.Count > 0)
            {
                if (!string.IsNullOrEmpty(vparts[0].Bitrate))
                    info.VideoBitrate = vparts[0].Bitrate;
                if (!string.IsNullOrEmpty(vparts[0].BitDepth))
                    info.VideoBitDepth = vparts[0].BitDepth;
                if (!string.IsNullOrEmpty(vparts[0].FrameRate))
                    info.VideoFrameRate = vparts[0].FrameRate;
            }
            List<Stream> aparts = m.Parts.SelectMany(a => a.Streams).Where(a => a.StreamType == "2").ToList();
            if (aparts.Count > 0)
            {
                if (!string.IsNullOrEmpty(aparts[0].Bitrate))
                    info.AudioBitrate = aparts[0].Bitrate;
            }
        }

        public bool RefreshMediaInfo()
        {
            try
            {
                logger.Trace("Getting media info for: {0}", FullServerPath ?? VideoLocal_Place_ID.ToString());
                Media m = null;
                List<Azure_Media> webmedias = AzureWebAPI.Get_Media(VideoLocal.ED2KHash);
                if (webmedias != null && webmedias.Count > 0 && webmedias.FirstOrDefault(a => a != null) != null)
                {
                    m = webmedias.FirstOrDefault(a => a != null).ToMedia();
                }
                if (m == null && FullServerPath != null)
                {
                    string name = (ImportFolder.CloudID == null)
                        ? FullServerPath.Replace("/", $"{Path.DirectorySeparatorChar}")
                        : ((IProvider) null).ReplaceSchemeHost(((IProvider) null).ConstructVideoLocalStream(0,
                            VideoLocalID.ToString(), "file", false));
                    m = MediaConvert.Convert(name, GetFile()); //Mediainfo should have libcurl.dll for http
                    if (string.IsNullOrEmpty(m?.Duration))
                        m = null;
                    if (m != null)
                        AzureWebAPI.Send_Media(VideoLocal.ED2KHash, m);
                }


                if (m != null)
                {
                    SVR_VideoLocal info = VideoLocal;
                    FillVideoInfoFromMedia(info, m);

                    m.Id = VideoLocalID.ToString();
                    List<Stream> subs = SubtitleHelper.GetSubtitleStreams(this);
                    if (subs.Count > 0)
                    {
                        m.Parts[0].Streams.AddRange(subs);
                    }
                    foreach (Part p in m.Parts)
                    {
                        p.Id = null;
                        p.Accessible = "1";
                        p.Exists = "1";
                        bool vid = false;
                        bool aud = false;
                        bool txt = false;
                        foreach (Stream ss in p.Streams.ToArray())
                        {
                            if (ss.StreamType == "1" && !vid) vid = true;
                            if (ss.StreamType == "2" && !aud)
                            {
                                aud = true;
                                ss.Selected = "1";
                            }
                            if (ss.StreamType == "3" && !txt)
                            {
                                txt = true;
                                ss.Selected = "1";
                            }
                        }
                    }
                    info.Media = m;
                    return true;
                }
                logger.Error($"File {FullServerPath ?? VideoLocal_Place_ID.ToString()} does not exist, unable to read media information from it");
            }
            catch (Exception e)
            {
                logger.Error($"Unable to read the media information of file {FullServerPath ?? VideoLocal_Place_ID.ToString()} ERROR: {e}");
            }
            return false;
        }

        public bool RemoveAndDeleteFile()
        {
            try
            {
                logger.Info("Deleting video local place record and file: {0}", (FullServerPath ?? VideoLocal_Place_ID.ToString()));

                IFileSystem fileSystem = ImportFolder?.FileSystem;
                if (fileSystem == null)
                {
                    logger.Error("Unable to delete file, filesystem not found. Removing record.");
                    RemoveRecord();
                    return true;
                }
                if (FullServerPath == null)
                {
                    logger.Error("Unable to delete file, fullserverpath is null. Removing record.");
                    RemoveRecord();
                    return true;
                }
                FileSystemResult<IObject> fr = fileSystem.Resolve(FullServerPath);
                if (fr == null || !fr.IsOk)
                {
                    logger.Error($"Unable to find file. Removing Record: {FullServerPath}");
                    RemoveRecord();
                    return true;
                }
                IFile file = fr.Result as IFile;
                if (file == null)
                {
                    logger.Error($"Seems '{FullServerPath}' is a directory.");
                    RemoveRecord();
                    return true;
                }
                FileSystemResult fs = file.Delete(false);
                if (fs == null || !fs.IsOk)
                {
                    logger.Error($"Unable to delete file '{FullServerPath}'");
                    return false;
                }
                RemoveRecord();
                // For deletion of files from Trakt, we will rely on the Daily sync
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                return false;
            }
        }

        public string RemoveAndDeleteFileWithMessage()
        {
            try
            {
                logger.Info("Deleting video local place record and file: {0}", (FullServerPath ?? VideoLocal_Place_ID.ToString()));

                IFileSystem fileSystem = ImportFolder?.FileSystem;
                if (fileSystem == null)
                {
                    logger.Error("Unable to delete file, filesystem not found. Removing record.");
                    RemoveRecord();
                    return "Unable to delete file, filesystem not found. Removing record.";
                }
                if (FullServerPath == null)
                {
                    logger.Error("Unable to delete file, fullserverpath is null. Removing record.");
                    RemoveRecord();
                    return "Unable to delete file, fullserverpath is null. Removing record.";
                }
                FileSystemResult<IObject> fr = fileSystem.Resolve(FullServerPath);
                if (fr == null || !fr.IsOk)
                {
                    logger.Error($"Unable to find file. Removing Record: {FullServerPath}");
                    RemoveRecord();
                    return $"Unable to find file. Removing Record: {FullServerPath}";
                }
                IFile file = fr.Result as IFile;
                if (file == null)
                {
                    logger.Error($"Seems '{FullServerPath}' is a directory.");
                    RemoveRecord();
                    return $"Seems '{FullServerPath}' is a directory.";
                }
                FileSystemResult fs = file.Delete(false);
                if (fs == null || !fs.IsOk)
                {
                    logger.Error($"Unable to delete file '{FullServerPath}'");
                    return $"Unable to delete file '{FullServerPath}'";
                }
                RemoveRecord();
                // For deletion of files from Trakt, we will rely on the Daily sync
                return "";
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                return ex.Message;
            }
        }

        public void RemoveAndDeleteFileWithOpenTransaction(ISession session, HashSet<SVR_AnimeEpisode> episodesToUpdate, HashSet<SVR_AnimeSeries> seriesToUpdate)
        {
            try
            {
                logger.Info("Deleting video local place record and file: {0}", (FullServerPath ?? VideoLocal_Place_ID.ToString()));

                IFileSystem fileSystem = ImportFolder?.FileSystem;
                if (fileSystem == null)
                {
                    logger.Error("Unable to delete file, filesystem not found. Removing record.");
                    RemoveRecordWithOpenTransaction(session, episodesToUpdate, seriesToUpdate);
                    return;
                }
                if (FullServerPath == null)
                {
                    logger.Error("Unable to delete file, fullserverpath is null. Removing record.");
                    RemoveRecordWithOpenTransaction(session, episodesToUpdate, seriesToUpdate);
                    return;
                }
                FileSystemResult<IObject> fr = fileSystem.Resolve(FullServerPath);
                if (fr == null || !fr.IsOk)
                {
                    logger.Error($"Unable to find file. Removing Record: {FullServerPath}");
                    RemoveRecordWithOpenTransaction(session, episodesToUpdate, seriesToUpdate);
                    return;
                }
                IFile file = fr.Result as IFile;
                if (file == null)
                {
                    logger.Error($"Seems '{FullServerPath}' is a directory.");
                    RemoveRecordWithOpenTransaction(session, episodesToUpdate, seriesToUpdate);
                    return;
                }
                FileSystemResult fs = file.Delete(false);
                if (fs == null || !fs.IsOk)
                {
                    logger.Error($"Unable to delete file '{FullServerPath}'");
                    return;
                }
                RemoveRecordWithOpenTransaction(session, episodesToUpdate, seriesToUpdate);
                // For deletion of files from Trakt, we will rely on the Daily sync
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        public void RenameAndMoveAsRequired()
        {
            bool succeeded = RenameIfRequired();
            if (!succeeded)
            {
                Thread.Sleep((int)DELAY_IN_USE.FIRST);
                succeeded = RenameIfRequired();
                if (!succeeded)
                {
                    Thread.Sleep((int) DELAY_IN_USE.SECOND);
                    succeeded = RenameIfRequired();
                    if (!succeeded)
                    {
                        Thread.Sleep((int) DELAY_IN_USE.THIRD);
                        succeeded = RenameIfRequired();
                        if (!succeeded)
                        {
                            // Don't bother moving if we can't rename
                            return;
                        }
                    }
                }
            }
            succeeded = MoveFileIfRequired();
            if (!succeeded)
            {
                Thread.Sleep((int)DELAY_IN_USE.FIRST);
                succeeded = MoveFileIfRequired();
                if (!succeeded)
                {
                    Thread.Sleep((int)DELAY_IN_USE.SECOND);
                    succeeded = MoveFileIfRequired();
                    if (!succeeded)
                    {
                        Thread.Sleep((int)DELAY_IN_USE.THIRD);
                        MoveFileIfRequired();
                        if (!succeeded) return; //Same as above, but linux permissiosn.
                    }
                }
            }

            Utilities.LinuxFS.SetLinuxPermissions(this.FullServerPath, ServerSettings.Linux_UID, ServerSettings.Linux_GID, ServerSettings.Linux_Permission);
        }

        // returns false if we should retry
        private bool RenameIfRequired()
        {
            try
            {
                return RenameFile();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                return true;
            }
        }

        public string MoveWithResultString(FileSystemResult<IObject> fileSystemResult, string scriptName, bool force = false)
        {
            // check if this file is in the drop folder
            // otherwise we don't need to move it
            if (ImportFolder.IsDropSource == 0 && !force)
            {
                logger.Error("Not moving file as it is NOT in the drop folder: {0}", FullServerPath);
                return "ERROR: Not in drop folder";
            }
            IFile source_file = fileSystemResult.Result as IFile;
            // We checked the above prior, so no error checking

            // There is a possibilty of weird logic based on source of the file. Some handling should be made for it....later
            (var destImpl, string newFullPath) = RenameFileHelper.GetRenamer(scriptName).GetDestinationFolder(this);

            if (!(destImpl is SVR_ImportFolder destFolder))
            {
                // In this case, an error string was returned, but we'll suppress it and give an error elsewhere
                if (newFullPath != null)
                {
                    logger.Error("Unable to find destination for: {0}", FullServerPath);
                    logger.Error("The error message was: " + newFullPath);
                    return "ERROR: " + newFullPath;
                }
                logger.Error("Unable to find destination for: {0}", FullServerPath);
                return "ERROR: There was an error but no error code returned...";
            }

            // keep the original drop folder for later (take a copy, not a reference)
            SVR_ImportFolder dropFolder = ImportFolder;

            if (string.IsNullOrEmpty(newFullPath))
            {
                logger.Error("Unable to find destination for: {0}", FullServerPath);
                return "ERROR: The returned path was null or empty";
            }

            // We've already resolved FullServerPath, so it doesn't need to be checked
            string relativeFilePath = Path.Combine(newFullPath, Path.GetFileName(FullServerPath));
            string newFullServerPath = Path.Combine(destFolder.ImportFolderLocation, relativeFilePath);

            IDirectory destination;

            fileSystemResult = destFolder.FileSystem.Resolve(Path.Combine(destFolder.ImportFolderLocation, newFullPath));
            if (fileSystemResult != null && fileSystemResult.IsOk)
            {
                destination = fileSystemResult.Result as IDirectory;
            }
            else
            {
                //validate the directory tree.
                destination = destFolder.BaseDirectory;
                {
                    var dir = Path.GetDirectoryName(relativeFilePath);

                    foreach (var part in dir.Split(Path.DirectorySeparatorChar))
                    {
                        var wD = destination.Directories.FirstOrDefault(d => d.Name == part);
                        if (wD == null)
                        {
                            var result = destination.CreateDirectory(part, null);
                            if (!result.IsOk)
                            {
                                logger.Error(
                                    $"Unable to create directory {part} in {destination.FullName}: {result.Error}");
                                return
                                    $"ERROR: Unable to create directory {part} in {destination.FullName}: {result.Error}";
                            }
                            destination = result.Result;
                            continue;
                        }

                        destination = wD;
                    }
                }
            }


            // Last ditch effort to ensure we aren't moving a file unto itself
            if (newFullServerPath.Equals(FullServerPath, StringComparison.InvariantCultureIgnoreCase))
            {
                return "The file is already at its desired location";
            }

            IFileSystem f = ImportFolder.FileSystem;
            FileSystemResult<IObject> dst = f.Resolve(newFullServerPath);
            if (dst != null && dst.IsOk)
            {
                return "ERROR: The File already exists at the destination";
            }
            else
            {
                logger.Info("Moving file from {0} to {1}", FullServerPath, newFullServerPath);
                FileSystemResult fr = source_file.Move(destination);
                if (fr == null || !fr.IsOk)
                {
                    logger.Error("Unable to MOVE file: {0} to {1} error {2}", FullServerPath,
                        newFullServerPath, fr?.Error ?? "No Error String");
                    return "ERROR: " + fr?.Error ?? "Error moving filebut no error string";
                }

                string originalFileName = FullServerPath;

                ImportFolderID = destFolder.ImportFolderID;
                FilePath = relativeFilePath;
                RepoFactory.VideoLocalPlace.Save(this);

                try
                {
                    // move any subtitle files
                    foreach (string subtitleFile in Utils.GetPossibleSubtitleFiles(originalFileName))
                    {
                        FileSystemResult<IObject> src = f.Resolve(subtitleFile);
                        if (src == null || !src.IsOk || !(src.Result is IFile)) continue;
                        string newSubPath = Path.Combine(Path.GetDirectoryName(newFullServerPath),
                            ((IFile) src.Result).Name);
                        dst = f.Resolve(newSubPath);
                        if (dst != null && dst.IsOk && dst.Result is IFile)
                        {
                            FileSystemResult fr2 = src.Result.Delete(false);
                            if (fr2 == null || !fr2.IsOk)
                            {
                                logger.Warn("Unable to DELETE file: {0} error {1}", subtitleFile,
                                    fr2?.Error ?? string.Empty);
                            }
                        }
                        else
                        {
                            FileSystemResult fr2 = ((IFile) src.Result).Move(destination);
                            if (fr2 == null || !fr2.IsOk)
                            {
                                logger.Error("Unable to MOVE file: {0} to {1} error {2}", subtitleFile,
                                    newSubPath, fr2?.Error ?? string.Empty);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, ex.ToString());
                }

                // check for any empty folders in drop folder
                // only for the drop folder
                if (dropFolder.IsDropSource == 1)
                {
                    FileSystemResult<IObject> dd = f.Resolve(dropFolder.ImportFolderLocation);
                    if (dd != null && dd.IsOk && dd.Result is IDirectory)
                        RecursiveDeleteEmptyDirectories((IDirectory) dd.Result, true);
                }
            }
            return newFullPath;
        }

        // returns false if we should retry
        private bool MoveFileIfRequired()
        {
            try
            {
                logger.Trace("Attempting to MOVE file: {0}", FullServerPath ?? VideoLocal_Place_ID.ToString());

                if (FullServerPath == null)
                {
                    logger.Error("Could not find or access the file to move: {0}",
                        VideoLocal_Place_ID);
                    return true;
                }

                // check if this file is in the drop folder
                // otherwise we don't need to move it
                if (ImportFolder.IsDropSource == 0)
                {
                    logger.Trace("Not moving file as it is NOT in the drop folder: {0}", FullServerPath);
                    return true;
                }
                IFileSystem f = ImportFolder.FileSystem;
                if (f == null)
                {
                    logger.Trace("Unable to MOVE, filesystem not working: {0}", FullServerPath);
                    return true;
                }

                FileSystemResult<IObject> fsrresult = f.Resolve(FullServerPath);
                if (fsrresult == null || !fsrresult.IsOk)
                {
                    logger.Error("Could not find or access the file to move: {0}", FullServerPath);
                    // this can happen due to file locks, so retry
                    return false;
                }
                IFile source_file = fsrresult.Result as IFile;
                if (source_file == null)
                {
                    logger.Error("Could not move the file (it isn't a file): {0}", FullServerPath);
                    // this means it isn't a file, but something else, so don't retry
                    return true;
                }

                // find the default destination
                (var destImpl, string newFullPath) = RenameFileHelper.GetRenamerWithFallback()?.GetDestinationFolder(this) ?? (null, null);

                if (!(destImpl is SVR_ImportFolder destFolder))
                {
                    // In this case, an error string was returned, but we'll suppress it and give an error elsewhere
                    if (newFullPath != null) return true;
                    logger.Error("Could not find a valid destination: {0}", FullServerPath);
                    return true;
                }

                // keep the original drop folder for later (take a copy, not a reference)
                SVR_ImportFolder dropFolder = ImportFolder;

                if (string.IsNullOrEmpty(newFullPath))
                {
                    return true;
                }

                // We've already resolved FullServerPath, so it doesn't need to be checked
                string relativeFilePath = Path.Combine(newFullPath, Path.GetFileName(FullServerPath));
                string newFullServerPath = Path.Combine(destFolder.ImportFolderLocation, relativeFilePath);

                IDirectory destination;

                fsrresult = destFolder.FileSystem.Resolve(Path.Combine(destFolder.ImportFolderLocation, newFullPath));
                if (fsrresult != null && fsrresult.IsOk)
                {
                    destination = fsrresult.Result as IDirectory;
                }
                else
                {
                    //validate the directory tree.
                    destination = destFolder.BaseDirectory;
                    {
                        var dir = Path.GetDirectoryName(relativeFilePath);

                        foreach (var part in dir.Split(Path.DirectorySeparatorChar))
                        {
                            var wD = destination.Directories.FirstOrDefault(d => d.Name == part);
                            if (wD == null)
                            {
                                var result = destination.CreateDirectory(part, null);
                                if (!result.IsOk)
                                {
                                    logger.Error(
                                        $"Unable to create directory {part} in {destination.FullName}: {result.Error}");
                                    return true;
                                }
                                destination = result.Result;
                                continue;
                            }

                            destination = wD;
                        }
                    }
                }


                // Last ditch effort to ensure we aren't moving a file unto itself
                if (newFullServerPath.Equals(FullServerPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    logger.Error($"Resolved to move {newFullServerPath} unto itself. NOT MOVING");
                    return true;
                }

                FileSystemResult<IObject> dst = f.Resolve(newFullServerPath);
                if (dst != null && dst.IsOk)
                {
                    logger.Info(
                        "Not moving file as it already exists at the new location, deleting source file instead: {0} --- {1}",
                        FullServerPath, newFullServerPath);

                    // if the file already exists, we can just delete the source file instead
                    // this is safer than deleting and moving
                    FileSystemResult fr = new FileSystemResult();
                    try
                    {
                        fr = source_file.Delete(false);
                        if (fr == null || !fr.IsOk)
                        {
                            logger.Warn("Unable to DELETE file: {0} error {1}", FullServerPath,
                                fr?.Error ?? string.Empty);
                            return false;
                        }
                        RemoveRecord();

                        // check for any empty folders in drop folder
                        // only for the drop folder
                        if (dropFolder.IsDropSource != 1) return true;
                        FileSystemResult<IObject> dd = f.Resolve(dropFolder.ImportFolderLocation);
                        if (dd != null && dd.IsOk && dd.Result is IDirectory)
                        {
                            RecursiveDeleteEmptyDirectories((IDirectory) dd.Result, true);
                        }
                        return true;
                    }
                    catch
                    {
                        logger.Error("Unable to DELETE file: {0} error {1}", FullServerPath,
                            fr?.Error ?? string.Empty);
                    }
                }
                else
                {
                    logger.Info("Moving file from {0} to {1}", FullServerPath, newFullServerPath);
                    FileSystemResult fr = source_file.Move(destination);
                    if (fr == null || !fr.IsOk)
                    {
                        logger.Error("Unable to MOVE file: {0} to {1} error {2}", FullServerPath,
                            newFullServerPath, fr?.Error ?? "No Error String");
                        return false;
                    }
/*
                    // Pause FileWatchDog
                    ShokoServer._pauseFileWatchDog.Reset();
                    foreach (System.IO.FileSystemEventArgs evt in ShokoServer.queueFileEvents)
                    {
                        try
                        {
                            // this shouldn't happend but w/e
                            if (evt?.ChangeType == System.IO.WatcherChangeTypes.Created)
                            {
                                // check if we know the fine by exact name
                                if (RepoFactory.VideoLocal.GetByName(Path.GetFileName(evt.Name)) != null)
                                {
                                    logger.Info("This file is known: {0}", evt.Name);
                                    // delete it from queue for hashing
                                    ShokoServer.queueFileEvents.Remove(evt);
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    // Resume FileWatchDog
                    ShokoServer._pauseFileWatchDog.Set();
*/
                    string originalFileName = FullServerPath;

                    ImportFolderID = destFolder.ImportFolderID;
                    FilePath = relativeFilePath;
                    RepoFactory.VideoLocalPlace.Save(this);

                    try
                    {
                        // move any subtitle files
                        foreach (string subtitleFile in Utils.GetPossibleSubtitleFiles(originalFileName))
                        {
                            FileSystemResult<IObject> src = f.Resolve(subtitleFile);
                            if (src == null || !src.IsOk || !(src.Result is IFile)) continue;
                            string newSubPath = Path.Combine(Path.GetDirectoryName(newFullServerPath),
                                ((IFile) src.Result).Name);
                            dst = f.Resolve(newSubPath);
                            if (dst != null && dst.IsOk && dst.Result is IFile)
                            {
                                FileSystemResult fr2 = src.Result.Delete(false);
                                if (fr2 == null || !fr2.IsOk)
                                {
                                    logger.Warn("Unable to DELETE file: {0} error {1}", subtitleFile,
                                        fr2?.Error ?? string.Empty);
                                }
                            }
                            else
                            {
                                FileSystemResult fr2 = ((IFile) src.Result).Move(destination);
                                if (fr2 == null || !fr2.IsOk)
                                {
                                    logger.Error("Unable to MOVE file: {0} to {1} error {2}", subtitleFile,
                                        newSubPath, fr2?.Error ?? string.Empty);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, ex.ToString());
                    }

                    // check for any empty folders in drop folder
                    // only for the drop folder
                    if (dropFolder.IsDropSource == 1)
                    {
                        FileSystemResult<IObject> dd = f.Resolve(dropFolder.ImportFolderLocation);
                        if (dd != null && dd.IsOk && dd.Result is IDirectory)
                            RecursiveDeleteEmptyDirectories((IDirectory) dd.Result, true);
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = $"Could not MOVE file: {FullServerPath ?? VideoLocal_Place_ID.ToString()} -- {ex}";
                logger.Error(ex, msg);
            }
            return true;
        }

        private void RecursiveDeleteEmptyDirectories(IDirectory dir, bool importfolder)
        {
            FileSystemResult fr = dir.Populate();
            if (fr.IsOk)
            {
                if (dir.Files.Count > 0 && dir.Directories.Count == 0)
                    return;
                foreach (IDirectory d in dir.Directories)
                    RecursiveDeleteEmptyDirectories(d, false);
            }
            if (importfolder)
                return;
            fr = dir.Populate();
            if (fr.IsOk)
            {
                if (dir.Files.Count == 0 && dir.Directories.Count == 0)
                {
                    fr = dir.Delete(true);
                    if (!fr.IsOk)
                    {
                        logger.Warn("Unable to DELETE directory: {0} error {1}", dir.FullName,
                            fr?.Error ?? String.Empty);
                    }
                }
            }
        }
    }
}