﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OpenUtau.Core.ResamplerDriver;
using OpenUtau.Core.USTx;
using Serilog;

namespace OpenUtau.Core.Render {

    internal class ResamplerInterface {
        private Action<SequencingSampleProvider> resampleDoneCallback;

        public void ResamplePart(UVoicePart part, UProject project, IResamplerDriver engine, Action<SequencingSampleProvider> resampleDoneCallback) {
            this.resampleDoneCallback = resampleDoneCallback;
            var worker = new BackgroundWorker {
                WorkerReportsProgress = true
            };
            worker.DoWork += Worker_DoWork;
            worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
            worker.ProgressChanged += Worker_ProgressChanged;
            worker.RunWorkerAsync(new Tuple<UVoicePart, UProject, IResamplerDriver>(part, project, engine));
        }

        private void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e) {
            DocManager.Inst.ExecuteCmd(new ProgressBarNotification(e.ProgressPercentage, (string)e.UserState), true);
        }

        private void Worker_DoWork(object sender, DoWorkEventArgs e) {
            var args = e.Argument as Tuple<UVoicePart, UProject, IResamplerDriver>;
            var part = args.Item1;
            var project = args.Item2;
            var engine = args.Item3;
            e.Result = RenderAsync(part, project, engine, sender as BackgroundWorker);
        }

        private void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e) {
            try {
                var renderItems = e.Result as List<RenderItem>;
                var renderItemSampleProviders = new List<RenderItemSampleProvider>();
                foreach (var item in renderItems) {
                    renderItemSampleProviders.Add(new RenderItemSampleProvider(item));
                }
                DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, string.Format(string.Empty)));
                resampleDoneCallback(new SequencingSampleProvider(renderItemSampleProviders));
            } catch (Exception ex) {
                Log.Error(ex, "Error while resampling.");
                resampleDoneCallback(null);
            }
        }

        private List<RenderItem> RenderAsync(UVoicePart part, UProject project, IResamplerDriver engine, BackgroundWorker worker) {
            var renderItems = new List<RenderItem>();
            var watch = new Stopwatch();
            watch.Start();
            Log.Information("Resampling start.");
            lock (part) {
                var cacheDir = PathManager.Inst.GetCachePath(project.FilePath);
                var cacheFiles = Directory.EnumerateFiles(cacheDir).ToArray();
                int count = 0, i = 0;
                foreach (var note in part.Notes) {
                    foreach (var phoneme in note.Phonemes) {
                        count++;
                    }
                }

                foreach (var note in part.Notes) {
                    foreach (var phoneme in note.Phonemes) {
                        if (string.IsNullOrEmpty(phoneme.Oto.File)) {
                            Log.Warning($"Cannot find phoneme in note {note.Lyric}");
                            continue;
                        }

                        var item = new RenderItem(phoneme, part, project);

                        // System.Diagnostics.Debug.WriteLine("Sound {0:x} resampling {1}", item.HashParameters(), item.GetResamplerExeArgs());
                        var engineArgs = DriverModels.CreateInputModel(item, 0);
                        var output = engine.DoResampler(engineArgs);
                        item.Sound = MemorySampleProvider.FromStream(output);
                        output.Dispose();
                        renderItems.Add(item);
                        worker.ReportProgress(100 * ++i / count, $"Resampling \"{phoneme.Phoneme}\" {i}/{count}");
                    }
                }
            }
            watch.Stop();
            Log.Information($"Resampling end, total time {watch.Elapsed}");
            return renderItems;
        }
    }
}
