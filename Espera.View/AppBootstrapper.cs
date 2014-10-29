﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Akavache;
using Akavache.Sqlite3;
using Caliburn.Micro;
using Espera.Core;
using Espera.Core.Analytics;
using Espera.Core.Management;
using Espera.Core.Mobile;
using Espera.Core.Settings;
using Espera.View.CacheMigration;
using Espera.View.ViewModels;
using NLog.Config;
using NLog.Targets;
using ReactiveUI;
using Splat;
using Squirrel;

namespace Espera.View
{
    internal class AppBootstrapper : BootstrapperBase, IEnableLogger
    {
        private CoreSettings coreSettings;
        private MobileApi mobileApi;
        private IDisposable updateSubscription;
        private ViewSettings viewSettings;

        static AppBootstrapper()
        {
            BlobCache.ApplicationName = AppInfo.AppName;
        }

        public AppBootstrapper()
        {
            this.Initialize();
        }

        protected override void Configure()
        {
            this.viewSettings = new ViewSettings();
            Locator.CurrentMutable.RegisterConstant(this.viewSettings, typeof(ViewSettings));

            this.coreSettings = new CoreSettings();

            Locator.CurrentMutable.RegisterLazySingleton(() => new Library(new LibraryFileReader(AppInfo.LibraryFilePath),
                new LibraryFileWriter(AppInfo.LibraryFilePath), this.coreSettings, new FileSystem()), typeof(Library));

            Locator.CurrentMutable.RegisterLazySingleton(() => new WindowManager(), typeof(IWindowManager));

            Locator.CurrentMutable.RegisterLazySingleton(() => new SQLitePersistentBlobCache(Path.Combine(AppInfo.BlobCachePath, "api-requests.cache.db")),
                typeof(IBlobCache), BlobCacheKeys.RequestCacheContract);

            Locator.CurrentMutable.RegisterLazySingleton(() =>
                new ShellViewModel(Locator.Current.GetService<Library>(),
                    this.viewSettings, this.coreSettings,
                    Locator.Current.GetService<IWindowManager>(),
                    Locator.Current.GetService<MobileApiInfo>()),
                typeof(ShellViewModel));

            this.ConfigureLogging();
        }

        protected override IEnumerable<object> GetAllInstances(Type serviceType)
        {
            return Locator.Current.GetServices(serviceType);
        }

        protected override object GetInstance(Type serviceType, string key)
        {
            return Locator.Current.GetService(serviceType, key);
        }

        protected override void OnExit(object sender, EventArgs e)
        {
            this.Log().Info("Starting Espera shutdown");

            this.Log().Info("Shutting down the library");
            Locator.Current.GetService<Library>().Dispose();

            this.Log().Info("Shutting down BlobCaches");
            BlobCache.Shutdown().Wait();
            var requestCache = Locator.Current.GetService<IBlobCache>(BlobCacheKeys.RequestCacheContract);
            requestCache.InvalidateAll().Wait();
            requestCache.Dispose();
            requestCache.Shutdown.Wait();

            this.Log().Info("Shutting down NLog");
            NLog.LogManager.Shutdown();

            if (this.mobileApi != null)
            {
                this.Log().Info("Shutting down mobile API");
                this.mobileApi.Dispose();
            }

            this.Log().Info("Shutting down analytics client");
            AnalyticsClient.Instance.Dispose();

            if (this.updateSubscription != null)
            {
                this.updateSubscription.Dispose();
            }

            this.Log().Info("Shutdown finished");
        }

        protected override void OnStartup(object sender, StartupEventArgs e)
        {
            this.Log().Info("Espera is starting...");
            this.Log().Info("******************************");
            this.Log().Info("**                          **");
            this.Log().Info("**          Espera          **");
            this.Log().Info("**                          **");
            this.Log().Info("******************************");
            this.Log().Info("Application version: " + AppInfo.Version);
            this.Log().Info("OS Version: " + Environment.OSVersion.VersionString);
            this.Log().Info("Current culture: " + CultureInfo.InstalledUICulture.Name);

            Directory.CreateDirectory(AppInfo.DirectoryPath);

#if DEBUG
            if (AppInfo.OverridenBasePath != null)
            {
                Directory.CreateDirectory(AppInfo.BlobCachePath);
                BlobCache.LocalMachine = new SQLitePersistentBlobCache(Path.Combine(AppInfo.BlobCachePath, "blobs.db"));
            }
#endif

            var newBlobCache = BlobCache.LocalMachine;

            if (AkavacheToSqlite3Migration.NeedsMigration(newBlobCache))
            {
                var oldBlobCache = new DeprecatedBlobCache(AppInfo.BlobCachePath);
                var migration = new AkavacheToSqlite3Migration(oldBlobCache, newBlobCache);

                migration.Run();

                this.Log().Info("Removing all items from old BlobCache");
                oldBlobCache.InvalidateAll().Wait();

                this.Log().Info("Shutting down old BlobCache");
                oldBlobCache.Dispose();
                this.Log().Info("BlobCache shutdown finished");
            }

            this.SetupLager();

            this.SetupAnalyticsClient();

            this.SetupMobileApi();

            this.SetupClickOnceUpdates();

            this.DisplayRootViewFor<ShellViewModel>();
        }

        protected override void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (Debugger.IsAttached)
                return;

            this.Log().FatalException("An unhandled exception occurred, opening the crash report", e.Exception);

            // MainWindow is sometimes null because of reasons
            if (this.Application.MainWindow != null)
            {
                this.Application.MainWindow.Hide();
            }

            var windowManager = Locator.Current.GetService<IWindowManager>();
            windowManager.ShowDialog(new CrashViewModel(e.Exception));

            e.Handled = true;

            Application.Current.Shutdown();
        }

        private void ConfigureLogging()
        {
            var logConfig = new LoggingConfiguration();

            var target = new FileTarget
            {
                FileName = AppInfo.LogFilePath,
                Layout = @"${longdate}|${logger}|${level}|${message} ${exception:format=ToString,StackTrace}",
                ArchiveAboveSize = 1024 * 1024 * 2, // 2 MB
                ArchiveNumbering = ArchiveNumberingMode.Sequence
            };

            logConfig.LoggingRules.Add(new LoggingRule("*", NLog.LogLevel.Info, target));
            NLog.LogManager.Configuration = logConfig;

            Locator.CurrentMutable.RegisterConstant(new NLogLogger(NLog.LogManager.GetCurrentClassLogger()), typeof(ILogger));
        }

        private void SetupAnalyticsClient()
        {
            AnalyticsClient.Instance.Initialize(this.coreSettings);
        }

        private void SetupClickOnceUpdates()
        {
            if (!AppInfo.IsPortable)
            {
                this.updateSubscription = Observable.Interval(TimeSpan.FromHours(2), RxApp.TaskpoolScheduler)
                    .StartWith(0) // Trigger an initial update check
                    .SelectMany(x => this.UpdateSilentlyAsync().ToObservable())
                    .Subscribe();
            }

            else
            {
                this.updateSubscription = Disposable.Empty;
            }
        }

        private void SetupLager()
        {
            this.Log().Info("Initializing Lager settings storages...");

            this.coreSettings.InitializeAsync().Wait();

            // If we don't have a path or it doesn't exist anymore, restore it.
            if (coreSettings.YoutubeDownloadPath == String.Empty || !Directory.Exists(coreSettings.YoutubeDownloadPath))
            {
                coreSettings.YoutubeDownloadPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

#if DEBUG
            coreSettings.EnableAutomaticReports = false;
#endif

            this.viewSettings.InitializeAsync().Wait();

            this.Log().Info("Settings storages initialized.");
        }

        private void SetupMobileApi()
        {
            var library = Locator.Current.GetService<Library>();

            this.Log().Info("Remote control is {0}", this.coreSettings.EnableRemoteControl ? "enabled" : "disabled");
            this.Log().Info("Port is set to {0}", this.coreSettings.Port);

            IObservable<MobileApi> apiChanged = this.coreSettings.WhenAnyValue(x => x.Port).DistinctUntilChanged()
                .CombineLatest(this.coreSettings.WhenAnyValue(x => x.EnableRemoteControl), Tuple.Create)
                .Do(_ =>
                {
                    if (this.mobileApi != null)
                    {
                        this.mobileApi.Dispose();
                        this.mobileApi = null;
                    }
                })
                .Where(x => x.Item2)
                .Select(x => x.Item1)
                .Select(x => new MobileApi(x, library)).Publish(null).RefCount().Where(x => x != null);

            apiChanged.Subscribe(x =>
            {
                this.mobileApi = x;
                x.SendBroadcastAsync();
                x.StartClientDiscovery();
            });

            IConnectableObservable<IReadOnlyList<MobileClient>> connectedClients = apiChanged.Select(x => x.ConnectedClients).Switch().Publish(new List<MobileClient>());
            connectedClients.Connect();

            IConnectableObservable<bool> isPortOccupied = apiChanged.Select(x => x.IsPortOccupied).Switch().Publish(false);
            isPortOccupied.Connect();

            var apiStats = new MobileApiInfo(connectedClients, isPortOccupied);

            Locator.CurrentMutable.RegisterConstant(apiStats, typeof(MobileApiInfo));
        }

        private async Task UpdateSilentlyAsync()
        {
            this.Log().Info("Looking for application updates");

            using (var updateManager = new UpdateManager("http://getespera.com/releases/squirrel/", "Espera", FrameworkVersion.Net45))
            {
                UpdateInfo updateInfo;

                try
                {
                    updateInfo = await updateManager.CheckForUpdate();
                }

                catch (Exception ex)
                {
                    this.Log().ErrorException("Error while checking for updates", ex);
                    return;
                }

                if (updateInfo.ReleasesToApply.Any())
                {
                    this.Log().Info("New version available: {0}", updateInfo.FutureReleaseEntry.Version);

                    Task changelogFetchTask = ChangelogFetcher.FetchAsync().ToObservable()
                    .Timeout(TimeSpan.FromSeconds(30))
                        .SelectMany(x => BlobCache.LocalMachine.InsertObject(BlobCacheKeys.Changelog, x))
                    .LoggedCatch(this, Observable.Return(Unit.Default), "Could not to fetch changelog")
                        .ToTask();

                    this.Log().Info("Applying updates...");

                    try
                    {
                        await updateManager.ApplyReleases(updateInfo);
                    }

                    catch (Exception ex)
                    {
                        this.Log().Fatal("Failed to apply updates.", ex);
                        AnalyticsClient.Instance.RecordNonFatalError(ex);
                        return;
                    }

                    await changelogFetchTask;

                    this.viewSettings.IsUpdated = true;

                    this.Log().Info("Updates applied.");
                }

                else
                {
                    this.Log().Info("No updates found");
                }
            }
        }
    }
}