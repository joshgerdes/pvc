﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Humanizer;
using Edokan.KaiZen.Colors;
using Minimatch;

namespace PvcCore
{
    public class PvcWatcher
    {
        public static List<PvcWatcherItem> Items = new List<PvcWatcherItem>();
        public static ConcurrentQueue<FileSystemEventArgs> EventQueue = new ConcurrentQueue<FileSystemEventArgs>();
        public static List<string> IgnoredGlobs = new List<string>();

        public static void RegisterWatchPipe(List<string> globs, List<Func<PvcPipe, PvcPipe>> pipeline, List<string> additionalFiles)
        {
            Items.Add(new PvcWatcherItem(globs, pipeline, additionalFiles));
        }

        public static void ListenForChanges(string currentDirectory)
        {
            var watcher = new FileSystemWatcher(currentDirectory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
                IncludeSubdirectories = true,
                InternalBufferSize = 16777216
            };

            watcher.BeginInit();
            watcher.Changed += watcher_Changed;
            watcher.Created += watcher_Changed;
            watcher.Deleted += watcher_Changed;
            watcher.Renamed += watcher_Changed;
            watcher.EndInit();

            while (true)
            {
                // begin event loop for watcher
                System.Threading.Thread.Sleep(100);
                ProcessQueueItem();
            }
        }

        static void ProcessQueueItem()
        {
            FileSystemEventArgs eventQueueItem = null;
            if (EventQueue.TryDequeue(out eventQueueItem))
            {
                foreach (var item in Items)
                {
                    var pipe = new PvcPipe();
                    var relativePath = PvcUtil.PathRelativeToCurrentDirectory(eventQueueItem.FullPath);

                    // if eventQueueItem.FullPath matches any items or their additional files, re-run that pipeline
                    var matchingFiles = pipe.FilterPaths(item.Globs);
                    var additionalFiles = pipe.FilterPaths(item.AdditionalFiles);

                    if (matchingFiles.Contains(relativePath) || additionalFiles.Contains(relativePath))
                    {
                        var newStreams = new List<PvcStream>();

                        // if its an 'additional file' match we need to run on the whole matching set to find the 'real'
                        // file that should be processed. Same if it was a deletion event.
                        if (additionalFiles.Contains(relativePath) || eventQueueItem.ChangeType == WatcherChangeTypes.Deleted)
                        {
                            newStreams.AddRange(matchingFiles.Select(x => PvcUtil.PathToStream(x)));
                        }
                        // otherwise we run against only the changed item
                        else
                        {
                            newStreams.Add(PvcUtil.PathToStream(eventQueueItem.FullPath));
                        }

                        pipe.streams = newStreams;

                        newStreams.ForEach(x => Console.WriteLine("Processing pipeline for '{0}'", x.StreamName.Magenta()));
                        var stopwatch = new Stopwatch();
                        stopwatch.Start();

                        foreach (var pipeline in item.Pipeline)
                        {
                            try
                            {
                                pipe = pipeline(pipe);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.Message);
                            }
                        }

                        stopwatch.Stop();
                        Console.WriteLine("Finished pipeline processing in {0}", stopwatch.Elapsed.Humanize().White());

                        newStreams.ForEach(x => x.UnloadStream());
                    }
                }
            }
        }

        static ConcurrentDictionary<string, DateTime> EventThrottles = new ConcurrentDictionary<string, DateTime>();

        static void watcher_Changed(object sender, FileSystemEventArgs e)
        {
            var matchOptions = new Options
            {
                AllowWindowsPaths = true,
                MatchBase = true,
                Dot = true,
                NoCase = true,
                NoNull = true
            };

            foreach (var ignoredGlob in IgnoredGlobs)
            {
                if (new Minimatcher(ignoredGlob, matchOptions).IsMatch(e.FullPath) || e.FullPath.StartsWith(ignoredGlob))
                {
                    return;
                }
            }

            var hasKey = EventThrottles.ContainsKey(e.FullPath);
            if (hasKey && EventThrottles[e.FullPath] > DateTime.Now.AddMilliseconds(-30))
            {
                return;
            }

            EventThrottles.AddOrUpdate(e.FullPath, DateTime.Now, (k, v) => DateTime.Now);
            PvcWatcher.EventQueue.Enqueue(e);
        }
    }

    public class PvcWatcherItem
    {
        public readonly List<string> Globs;
        public readonly List<Func<PvcPipe, PvcPipe>> Pipeline;
        public readonly List<string> AdditionalFiles;

        public PvcWatcherItem(List<string> globs, List<Func<PvcPipe, PvcPipe>> pipeline, List<string> additionalFiles)
        {
            this.Globs = globs;
            this.Pipeline = pipeline;
            this.AdditionalFiles = additionalFiles;
        }
    }
}
