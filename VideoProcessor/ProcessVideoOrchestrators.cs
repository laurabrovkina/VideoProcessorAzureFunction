﻿using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoProcessor
{
    public static class ProcessVideoOrchestrators
    {
        [FunctionName("O_ProcessVideo")]
        public static async Task<object> ProcessVideo(
            [OrchestrationTrigger] DurableOrchestrationContext ctx,
            TraceWriter log)
        {
            var videoLocation = ctx.GetInput<string>();

            if (!ctx.IsReplaying)
                log.Info("About to call transcode video activity");

            string transcodedLocation = null;
            string thumbnailLocation = null;
            string withIntroLocation = null;

            string approvalResult = "Unknown";

            try
            {
                var transeCodeResults =
                    await ctx.CallSubOrchestratorAsync<VideoFileInfo[]>("O_TranscodeVideo", videoLocation);

                transcodedLocation = transeCodeResults
                        .OrderByDescending(r => r.BitRate)
                        .Select(r => r.Location)
                        .First();

                if (!ctx.IsReplaying)
                    log.Info("About to call extract thumbnail");

                thumbnailLocation = await
                    ctx.CallActivityWithRetryAsync<string>("A_ExtractThumbnail", new RetryOptions(TimeSpan.FromSeconds(1), 2),
                    transcodedLocation);

                if (!ctx.IsReplaying)
                    log.Info("About to call prepend intro");

                withIntroLocation = await
                    ctx.CallActivityWithRetryAsync<string>("A_PrependIntro", new RetryOptions(TimeSpan.FromSeconds(1), 2),
                    transcodedLocation);

                await ctx.CallActivityAsync("A_SendApprovalRequestEmail", new ApprovalInfo()
                {
                    OrchestrationId = ctx.InstanceId,
                    VideoLocation = withIntroLocation
                });

                using (var cts = new CancellationTokenSource())
                {
                    var timeoutAt = ctx.CurrentUtcDateTime.AddSeconds(30);
                    var timeoutTask = ctx.CreateTimer(timeoutAt, cts.Token);
                    var approvalTask = ctx.WaitForExternalEvent<string>("ApprovalResult");

                    var winner = await Task.WhenAny(approvalTask, timeoutTask);
                    if (winner == approvalTask)
                    {
                        approvalResult = approvalTask.Result;
                        cts.Cancel(); // we should cancel the timeout task
                    }
                    else
                    {
                        approvalResult = "Timed Out";
                    }
                }

                if (approvalResult == "Approved")
                {
                    await ctx.CallActivityAsync("A_PublishVideo", withIntroLocation);
                }
                else
                {
                    await ctx.CallActivityAsync("A_RejectVideo", withIntroLocation);
                }
            }
            catch (Exception e)
            {
                if (!ctx.IsReplaying)
                    log.Info($"Caught an error from an activity: {e.Message}");

                await
                    ctx.CallActivityAsync<string>("A_Cleanup",
                        new[] { transcodedLocation, thumbnailLocation, withIntroLocation });

                return new
                {
                    Error = "Failed to process uploaded video",
                    Message = e.Message
                };
            }
            return new
            {
                Transcoded = transcodedLocation,
                Thumbnail = thumbnailLocation,
                WithIntro = withIntroLocation,
                ApprovalResult = approvalResult
            };

        }

        [FunctionName("O_TranscodeVideo")]
        public static async Task<VideoFileInfo[]> TranscodeVideo(
            [OrchestrationTrigger] DurableOrchestrationContext ctx,
            TraceWriter log)
        {
            var videoLocation = ctx.GetInput<string>();
            var bitRates = await ctx.CallActivityAsync<int[]>("A_GetTranseCodeBitrates", null);
            var transeCodeTasks = new List<Task<VideoFileInfo>>();

            foreach (var bitRate in bitRates)
            {
                var info = new VideoFileInfo { BitRate = bitRate, Location = videoLocation };
                var task =
                    ctx.CallActivityWithRetryAsync<VideoFileInfo>("A_TranscodeVideo", 
                    new RetryOptions(TimeSpan.FromSeconds(1), 2),
                    info);
                transeCodeTasks.Add(task);
            }

            var transeCodeResults = await Task.WhenAll(transeCodeTasks);
            return transeCodeResults;
        }
    }
}
