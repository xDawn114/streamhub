package com.limelight.streamhub;

import android.app.job.JobInfo;
import android.app.job.JobParameters;
import android.app.job.JobScheduler;
import android.app.job.JobService;
import android.content.ComponentName;
import android.content.Context;

/** OS-scheduled checks; ordinary sideloaded apps still use the system installation confirmation. */
public final class HubUpdateJobService extends JobService {
    private static final int JOB = 7406;
    private boolean started;
    public static void schedule(Context context, boolean enabled) {
        JobScheduler scheduler = (JobScheduler)context.getSystemService(Context.JOB_SCHEDULER_SERVICE);
        if (!enabled) { scheduler.cancel(JOB); return; }
        if (scheduler.getPendingJob(JOB) != null) return;
        scheduler.schedule(new JobInfo.Builder(JOB, new ComponentName(context, HubUpdateJobService.class))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY).setPersisted(true)
                .setPeriodic(HubUpdateSchedule.INTERVAL).setBackoffCriteria(5 * 60000L, JobInfo.BACKOFF_POLICY_EXPONENTIAL).build());
    }
    @Override public boolean onStartJob(JobParameters parameters) {
        HubUpdates updates = HubUpdates.get(this);
        if (!updates.autoCheck() || !updates.checkDue() && !updates.lastCheckFailed() || updates.deviceBusy()) return false;
        started = updates.checkFromJob(() -> { if (started) { started = false; jobFinished(parameters, updates.autoCheck() && updates.lastCheckFailed()); } });
        return started;
    }
    @Override public boolean onStopJob(JobParameters parameters) {
        if (started) HubUpdates.get(this).cancel();
        started = false;
        return true;
    }
}
