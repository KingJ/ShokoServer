﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NLog;
using Shoko.Commons.Extensions;
using Shoko.Commons.Notification;
using Shoko.Commons.Queue;
using Shoko.Models;
using Shoko.Models.Enums;
using Shoko.Models.Queue;
using Shoko.Models.Server;
using Shoko.Server.Commands;
using Shoko.Server.Databases;
using Shoko.Server.Models;
using Shoko.Server.FileHelper;
using Shoko.Server.PlexAndKodi;
using Shoko.Server.Repositories;

namespace Shoko.Server
{
    public class Scanner : INotifyPropertyChangedExt
    {
        private BackgroundWorker workerIntegrityScanner = new BackgroundWorker();

        public Scanner()
        {
            workerIntegrityScanner.WorkerReportsProgress = true;
            workerIntegrityScanner.WorkerSupportsCancellation = true;
            workerIntegrityScanner.DoWork += WorkerIntegrityScanner_DoWork;
        }

        public static Scanner Instance { get; set; } = new Scanner();

        public event PropertyChangedEventHandler PropertyChanged;

        public void NotifyPropertyChanged(string propname)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propname));
        }

        private int queueCount = 0;

        public int QueueCount
        {
            get { return queueCount; }
            set { this.SetField(() => queueCount, value); }
        }

        public void Init()
        {
            Application.Current.Dispatcher.Invoke(() => { RepoFactory.Scan.GetAll().ForEach(a => Scans.Add(a)); });
            SVR_Scan runscan = Scans.FirstOrDefault(a => a.GetScanStatus() == ScanStatus.Running);
            if (runscan != null)
            {
                ActiveScan = runscan;
                StartScan();
            }
        }

        public void StartScan()
        {
            if (ActiveScan == null)
                return;
            RunScan = ActiveScan;
            cancelIntegrityCheck = false;
            workerIntegrityScanner.RunWorkerAsync();
        }

        public void ClearScan()
        {
            if (ActiveScan == null)
                return;
            if (workerIntegrityScanner.IsBusy && RunScan == ActiveScan)
                CancelScan();
            RepoFactory.ScanFile.Delete(RepoFactory.ScanFile.GetByScanID(ActiveScan.ScanID));
            RepoFactory.Scan.Delete(ActiveScan);
            Application.Current.Dispatcher.Invoke(() => { Scans.Remove(ActiveScan); });
            ActiveScan = null;
        }

        public void DoEvents()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new DispatcherOperationCallback(ExitFrame), frame);
            Dispatcher.PushFrame(frame);
        }

        public object ExitFrame(object f)
        {
            ((DispatcherFrame) f).Continue = false;

            return null;
        }

        public void CancelScan()
        {
            if (ActiveScan == null)
                return;
            if (workerIntegrityScanner.IsBusy)
            {
                cancelIntegrityCheck = true;
                while (workerIntegrityScanner.IsBusy)
                {
                    DoEvents();
                    Thread.Sleep(100);
                }
                cancelIntegrityCheck = false;
            }
        }

        public bool Finished => (ActiveScan != null && ActiveScan.GetScanStatus() == ScanStatus.Finish) ||
                                ActiveScan == null;

        public string QueueState => ActiveScan != null ? ActiveScan.GetStatusText() : string.Empty;
        public bool QueuePaused => ActiveScan != null && ActiveScan.GetScanStatus() == ScanStatus.Standby;
        public bool QueueRunning => ActiveScan != null && ActiveScan.GetScanStatus() == ScanStatus.Running;
        public bool Exists => (ActiveScan != null);
        private SVR_Scan activeScan;

        public SVR_Scan ActiveScan
        {
            get { return activeScan; }
            set
            {
                if (value != activeScan)
                {
                    activeScan = value;
                    Refresh();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ActiveErrorFiles.Clear();
                        if (value != null)
                            RepoFactory.ScanFile.GetWithError(value.ScanID).ForEach(a => ActiveErrorFiles.Add(a));
                    });
                }
            }
        }

        public void Refresh()
        {
            this.OnPropertyChanged(() => Exists, () => Finished, () => QueueState, () => QueuePaused,
                () => QueueRunning);
            if (activeScan != null)
                QueueCount = RepoFactory.ScanFile.GetWaitingCount(activeScan.ScanID);
        }

        public ObservableCollection<SVR_Scan> Scans { get; set; } = new ObservableCollection<SVR_Scan>();

        public ObservableCollection<ScanFile> ActiveErrorFiles { get; set; } = new ObservableCollection<ScanFile>();

        public bool HasFiles => Finished && ActiveErrorFiles.Count > 0;

        public void AddErrorScan(ScanFile file)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (ActiveScan != null && ActiveScan.ScanID == file.ScanID)
                    ActiveErrorFiles.Add(file);
            });
        }

        public void DeleteAllErroredFiles()
        {
            if (ActiveScan == null) return;
            var files = ActiveErrorFiles.ToList();
            ActiveErrorFiles.Clear();
            HashSet<SVR_AnimeEpisode> episodesToUpdate = new HashSet<SVR_AnimeEpisode>();
            HashSet<SVR_AnimeSeries> seriesToUpdate = new HashSet<SVR_AnimeSeries>();
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                files.ForEach(file =>
                {
                    SVR_VideoLocal_Place place = RepoFactory.VideoLocalPlace.GetByID(file.VideoLocal_Place_ID);
                    place.RemoveAndDeleteFileWithOpenTransaction(session, episodesToUpdate, seriesToUpdate);
                });
                // update everything we modified
                foreach (SVR_AnimeEpisode ep in episodesToUpdate)
                {
                    if (ep.AnimeEpisodeID == 0)
                    {
                        ep.PlexContract = null;
                        RepoFactory.AnimeEpisode.Save(ep);
                    }
                    try
                    {
                        ep.PlexContract = Helper.GenerateVideoFromAnimeEpisode(ep);
                        RepoFactory.AnimeEpisode.SaveWithOpenTransaction(session, ep);
                    }
                    catch (Exception ex)
                    {
                        LogManager.GetCurrentClassLogger().Error(ex, ex.ToString());
                    }
                }
                foreach (SVR_AnimeSeries ser in seriesToUpdate)
                {
                    ser.QueueUpdateStats();
                }
            }
            RepoFactory.ScanFile.Delete(files);
        }

        private bool cancelIntegrityCheck = false;
        internal SVR_Scan RunScan;

        public static int OnHashProgress(string fileName, int percentComplete)
        {
            return 1; //continue hashing (return 0 to abort)
        }

        private void WorkerIntegrityScanner_DoWork(object sender, DoWorkEventArgs e)
        {
            if (RunScan != null && RunScan.GetScanStatus() != ScanStatus.Finish)
            {
                bool paused = ShokoService.CmdProcessorHasher.Paused;
                ShokoService.CmdProcessorHasher.Paused = true;
                SVR_Scan s = RunScan;
                s.Status = (int) ScanStatus.Running;
                RepoFactory.Scan.Save(s);
                Refresh();
                List<ScanFile> files = RepoFactory.ScanFile.GetWaiting(s.ScanID);
                int cnt = 0;
                foreach (ScanFile sf in files)
                {
                    try
                    {
                        if (!File.Exists(sf.FullName))
                            sf.Status = (int) ScanFileStatus.ErrorFileNotFound;
                        else
                        {
                            FileInfo f = new FileInfo(sf.FullName);
                            if (sf.FileSize != f.Length)
                                sf.Status = (int) ScanFileStatus.ErrorInvalidSize;
                            else
                            {
                                ShokoService.CmdProcessorHasher.QueueState = new QueueStateStruct()
                                {
                                    queueState = QueueStateEnum.HashingFile,
                                    extraParams = new[] { sf.FullName }
                                };
                                Hashes hashes =
                                    FileHashHelper.GetHashInfo(sf.FullName, true, OnHashProgress, false, false, false);
                                if (string.IsNullOrEmpty(hashes.ED2K))
                                {
                                    sf.Status = (int) ScanFileStatus.ErrorMissingHash;
                                }
                                else
                                {
                                    sf.HashResult = hashes.ED2K;
                                    if (!sf.Hash.Equals(sf.HashResult, StringComparison.InvariantCultureIgnoreCase))
                                        sf.Status = (int) ScanFileStatus.ErrorInvalidHash;
                                    else
                                        sf.Status = (int) ScanFileStatus.ProcessedOK;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        sf.Status = (int) ScanFileStatus.ErrorIOError;
                    }
                    cnt++;
                    sf.CheckDate = DateTime.Now;
                    RepoFactory.ScanFile.Save(sf);
                    if (sf.Status > (int) ScanFileStatus.ProcessedOK)
                        Scanner.Instance.AddErrorScan(sf);
                    Refresh();

                    if (cancelIntegrityCheck)
                        break;
                }
                if (files.Any(a => a.GetScanFileStatus() == ScanFileStatus.Waiting))
                    s.Status = (int) ScanStatus.Standby;
                else
                    s.Status = (int) ScanStatus.Finish;
                RepoFactory.Scan.Save(s);
                Refresh();
                RunScan = null;
                ShokoService.CmdProcessorHasher.Paused = paused;
            }
        }
    }
}