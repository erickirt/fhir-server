﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.JobManagement.UnitTests
{
    public class TestQueueClient : IQueueClient
    {
        private List<JobInfo> jobInfos = new List<JobInfo>();
        private long largestId = 1;

        public Action DequeueFaultAction { get; set; }

        public Action HeartbeatFaultAction { get; set; }

        public Action CompleteFaultAction { get; set; }

        public Func<TestQueueClient, long, CancellationToken, JobInfo> GetJobByIdFunc { get; set; }

        public Func<TestQueueClient, long, CancellationToken, IReadOnlyList<JobInfo>> GetJobByGroupIdFunc { get; set; }

        public Func<TestQueueClient, CancellationToken, IReadOnlyList<JobInfo>> GetJobByQueueTypeFunc { get; set; }

        public List<JobInfo> JobInfos
        {
            get { return jobInfos; }
        }

        public Task CancelJobByGroupIdAsync(byte queueType, long groupId, CancellationToken cancellationToken)
        {
            foreach (JobInfo jobInfo in jobInfos.Where(t => t.GroupId == groupId))
            {
                if (jobInfo.Status == JobStatus.Created)
                {
                    jobInfo.Status = JobStatus.Cancelled;
                }

                if (jobInfo.Status == JobStatus.Running)
                {
                    jobInfo.CancelRequested = true;
                }
            }

            return Task.CompletedTask;
        }

        public Task CancelJobByIdAsync(byte queueType, long jobId, CancellationToken cancellationToken)
        {
            foreach (JobInfo jobInfo in jobInfos.Where(t => t.Id == jobId))
            {
                if (jobInfo.Status == JobStatus.Created)
                {
                    jobInfo.Status = JobStatus.Cancelled;
                }

                if (jobInfo.Status == JobStatus.Running)
                {
                    jobInfo.CancelRequested = true;
                }
            }

            return Task.CompletedTask;
        }

        public async Task CompleteJobAsync(JobInfo jobInfo, bool requestCancellationOnFailure, CancellationToken cancellationToken)
        {
            CompleteFaultAction?.Invoke();

            JobInfo jobInfoStore = jobInfos.FirstOrDefault(t => t.Id == jobInfo.Id);
            jobInfoStore.Status = jobInfo.Status;
            jobInfoStore.Result = jobInfo.Result;

            if (requestCancellationOnFailure && jobInfo.Status == JobStatus.Failed)
            {
                await CancelJobByGroupIdAsync(jobInfo.QueueType, jobInfo.GroupId, cancellationToken);
            }
        }

        public async Task<IReadOnlyCollection<JobInfo>> DequeueJobsAsync(byte queueType, int numberOfJobsToDequeue, string worker, int heartbeatTimeoutSec, CancellationToken cancellationToken)
        {
            var dequeuedJobs = new List<JobInfo>();

            while (dequeuedJobs.Count < numberOfJobsToDequeue)
            {
                var jobInfo = await DequeueAsync(queueType, worker, heartbeatTimeoutSec, cancellationToken);

                if (jobInfo != null)
                {
                    dequeuedJobs.Add(jobInfo);
                }
                else
                {
                    // No more jobs in queue
                    break;
                }
            }

            return dequeuedJobs;
        }

        public Task<JobInfo> DequeueAsync(byte queueType, string worker, int heartbeatTimeoutSec, CancellationToken cancellationToken, long? jobId = null, bool checkTimeoutJobsOnly = false)
        {
            DequeueFaultAction?.Invoke();
            JobInfo job = null;
            lock (jobInfos)
            {
                job = jobInfos.FirstOrDefault(t =>
                    t.QueueType == queueType &&
                    (t.Status == JobStatus.Created ||
                    (t.Status == JobStatus.Running && (DateTime.Now - t.HeartbeatDateTime) > TimeSpan.FromSeconds(heartbeatTimeoutSec))));
                if (job != null)
                {
                    job.Status = JobStatus.Running;
                    job.HeartbeatDateTime = DateTime.Now;
                }
            }

            return Task.FromResult(job);
        }

        public Task<IReadOnlyList<JobInfo>> EnqueueAsync(byte queueType, string[] definitions, long? groupId, bool forceOneActiveJobGroup, CancellationToken cancellationToken)
        {
            var result = new List<JobInfo>();

            long gId = groupId ?? largestId++;

            foreach (string definition in definitions)
            {
                if (jobInfos.Any(t => t.Definition.Equals(definition)))
                {
                    result.Add(jobInfos.First(t => t.Definition.Equals(definition)));
                    continue;
                }
                else
                {
                    var newJob = new JobInfo()
                    {
                        Definition = definition,
                        Id = largestId,
                        GroupId = gId,
                        Status = JobStatus.Created,
                        HeartbeatDateTime = DateTime.Now,
                        QueueType = queueType,
                    };

                    if (newJob.Status == JobStatus.Created)
                    {
                        newJob.CreateDate = DateTime.Now;
                    }

                    result.Add(newJob);
                    jobInfos.Add(newJob);
                }

                largestId++;
            }

            return Task.FromResult<IReadOnlyList<JobInfo>>(result);
        }

        public Task<IReadOnlyList<JobInfo>> GetJobByGroupIdAsync(byte queueType, long groupId, bool returnDefinition, CancellationToken cancellationToken)
        {
            if (GetJobByGroupIdFunc != null)
            {
                return Task.FromResult(GetJobByGroupIdFunc(this, groupId, cancellationToken));
            }

            IReadOnlyList<JobInfo> result = jobInfos.Where(t => t.GroupId == groupId).ToList();
            return Task.FromResult(result);
        }

        public Task<JobInfo> GetJobByIdAsync(byte queueType, long jobId, bool returnDefinition, CancellationToken cancellationToken)
        {
            if (GetJobByIdFunc != null)
            {
                return Task.FromResult(GetJobByIdFunc(this, jobId, cancellationToken));
            }

            JobInfo result = jobInfos.FirstOrDefault(t => t.Id == jobId);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<JobInfo>> GetJobsByIdsAsync(byte queueType, long[] jobIds, bool returnDefinition, CancellationToken cancellationToken)
        {
            if (GetJobByIdFunc != null)
            {
                return Task.FromResult((IReadOnlyList<JobInfo>)jobIds.Select(jobId => GetJobByIdFunc(this, jobId, cancellationToken)).ToList());
            }

            IReadOnlyList<JobInfo> result = jobInfos.Where(t => jobIds.Contains(t.Id)).ToList();
            return Task.FromResult(result);
        }

        public bool IsInitialized()
        {
            return true;
        }

        public Task<bool> PutJobHeartbeatAsync(JobInfo jobInfo, CancellationToken cancellationToken)
        {
            var cancel = false;
            try
            {
                HeartbeatFaultAction?.Invoke();

                JobInfo job = jobInfos.FirstOrDefault(t => t.Id == jobInfo.Id);
                if (job == null)
                {
                    throw new JobNotExistException("not exist");
                }

                job.HeartbeatDateTime = DateTime.Now;
                job.Result = jobInfo.Result;

                cancel = job.CancelRequested;
            }
            catch
            {
                // do nothing
            }

            return Task.FromResult(cancel);
        }
    }
}
