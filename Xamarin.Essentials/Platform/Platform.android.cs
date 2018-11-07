﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware;
using Android.Hardware.Camera2;
using Android.Locations;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;

namespace Xamarin.Essentials
{
    public static partial class Platform
    {
        static ActivityLifecycleContextListener lifecycleListener;

        internal static Context AppContext =>
            Application.Context;

        internal static event Action<int, Result, Intent> ActivityResult;

        internal static Activity GetCurrentActivity(bool throwOnNull)
        {
            var activity = lifecycleListener?.Activity;
            if (throwOnNull && activity == null)
                throw new NullReferenceException("The current Activity can not be detected. Ensure that you have called Init in your Activity or Application class.");

            return activity;
        }

        public static void Init(Application application)
        {
            lifecycleListener = new ActivityLifecycleContextListener();
            application.RegisterActivityLifecycleCallbacks(lifecycleListener);
        }

        public static void Init(Activity activity, Bundle bundle)
        {
            Init(activity.Application);
            lifecycleListener.Activity = activity;
        }

        public static void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults) =>
            Permissions.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        public static void OnActivityResult(int requestCode, Result resultCode, Intent data) =>
            ActivityResult?.Invoke(requestCode, resultCode, data);

        internal static bool HasSystemFeature(string systemFeature)
        {
            var packageManager = AppContext.PackageManager;
            foreach (var feature in packageManager.GetSystemAvailableFeatures())
            {
                if (feature.Name.Equals(systemFeature, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static bool IsIntentSupported(Intent intent)
        {
            var manager = AppContext.PackageManager;
            var activities = manager.QueryIntentActivities(intent, PackageInfoFlags.MatchDefaultOnly);
            return activities.Any();
        }

        internal static bool HasApiLevel(BuildVersionCodes versionCode) =>
            (int)Build.VERSION.SdkInt >= (int)versionCode;

        internal static CameraManager CameraManager =>
            AppContext.GetSystemService(Context.CameraService) as CameraManager;

        internal static ConnectivityManager ConnectivityManager =>
            AppContext.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

        internal static Vibrator Vibrator =>
            AppContext.GetSystemService(Context.VibratorService) as Vibrator;

        internal static WifiManager WifiManager =>
            AppContext.GetSystemService(Context.WifiService) as WifiManager;

        internal static SensorManager SensorManager =>
            AppContext.GetSystemService(Context.SensorService) as SensorManager;

        internal static ClipboardManager ClipboardManager =>
            AppContext.GetSystemService(Context.ClipboardService) as ClipboardManager;

        internal static LocationManager LocationManager =>
            AppContext.GetSystemService(Context.LocationService) as LocationManager;

        internal static PowerManager PowerManager =>
            AppContext.GetSystemService(Context.PowerService) as PowerManager;

        internal static Java.Util.Locale GetLocale()
        {
            var resources = AppContext.Resources;
            var config = resources.Configuration;
            if (HasApiLevel(BuildVersionCodes.N))
                return config.Locales.Get(0);

            return config.Locale;
        }

        internal static void SetLocale(Java.Util.Locale locale)
        {
            Java.Util.Locale.Default = locale;
            var resources = AppContext.Resources;
            var config = resources.Configuration;
            if (HasApiLevel(BuildVersionCodes.N))
                config.SetLocale(locale);
            else
                config.Locale = locale;

#pragma warning disable CS0618 // Type or member is obsolete
            resources.UpdateConfiguration(config, resources.DisplayMetrics);
#pragma warning restore CS0618 // Type or member is obsolete
        }
    }

    class ActivityLifecycleContextListener : Java.Lang.Object, Application.IActivityLifecycleCallbacks
    {
        WeakReference<Activity> currentActivity = new WeakReference<Activity>(null);

        internal Context Context =>
            Activity ?? Application.Context;

        internal Activity Activity
        {
            get => currentActivity.TryGetTarget(out var a) ? a : null;
            set => currentActivity.SetTarget(value);
        }

        void Application.IActivityLifecycleCallbacks.OnActivityCreated(Activity activity, Bundle savedInstanceState) =>
            Activity = activity;

        void Application.IActivityLifecycleCallbacks.OnActivityDestroyed(Activity activity)
        {
        }

        void Application.IActivityLifecycleCallbacks.OnActivityPaused(Activity activity) =>
            Activity = activity;

        void Application.IActivityLifecycleCallbacks.OnActivityResumed(Activity activity) =>
            Activity = activity;

        void Application.IActivityLifecycleCallbacks.OnActivitySaveInstanceState(Activity activity, Bundle outState)
        {
        }

        void Application.IActivityLifecycleCallbacks.OnActivityStarted(Activity activity)
        {
        }

        void Application.IActivityLifecycleCallbacks.OnActivityStopped(Activity activity)
        {
        }
    }

    [Activity(ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    class IntermediateActivity : Activity
    {
        const string launchedExtra = "launched";
        const string actualIntentExtra = "actual_intent";
        const string guidExtra = "guid";
        const string requestCodeExtra = "request_code";

        static readonly ConcurrentDictionary<string, TaskCompletionSource<Intent>> pendingTasks = new ConcurrentDictionary<string, TaskCompletionSource<Intent>>();

        bool launched;
        Intent actualIntent;
        string guid;
        int requestCode;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var extras = savedInstanceState ?? Intent.Extras;

            // read the values
            launched = extras.GetBoolean(launchedExtra, false);
            actualIntent = extras.GetParcelable(actualIntentExtra) as Intent;
            guid = extras.GetString(guidExtra);
            requestCode = extras.GetInt(requestCodeExtra, -1);

            // if this is the first time, lauch the real activity
            if (!launched)
            {
                StartActivityForResult(actualIntent, requestCode);
            }
        }

        protected override void OnSaveInstanceState(Bundle outState)
        {
            // make sure we mark this activity as launched
            outState.PutBoolean(launchedExtra, true);

            // save the values
            outState.PutParcelable(actualIntentExtra, actualIntent);
            outState.PutString(guidExtra, guid);
            outState.PutInt(requestCodeExtra, requestCode);

            base.OnSaveInstanceState(outState);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            // we have a valid GUID, so handle the task
            if (!string.IsNullOrEmpty(guid) && pendingTasks.TryRemove(guid, out var tcs) && tcs != null)
            {
                if (resultCode == Result.Canceled)
                    tcs.TrySetCanceled();
                else
                    tcs.TrySetResult(data);
            }

            // raise the global event
            Platform.OnActivityResult(requestCode, resultCode, data);

            // close the intermediate activity
            Finish();
        }

        public static Task<Intent> StartAsync(Intent intent, int requestCode)
        {
            // make sure we have the activity
            var activity = Platform.GetCurrentActivity(true);

            var tcs = new TaskCompletionSource<Intent>();

            // create a new task
            var guid = Guid.NewGuid().ToString();
            pendingTasks[guid] = tcs;

            // create the intermediate intent, and add the real intent to it
            var intermediateIntent = new Intent(activity, typeof(IntermediateActivity));
            intermediateIntent.PutExtra(actualIntentExtra, intent);
            intermediateIntent.PutExtra(guidExtra, guid);
            intermediateIntent.PutExtra(requestCodeExtra, requestCode);

            // start the intermediate activity
            activity.StartActivityForResult(intermediateIntent, requestCode);

            return tcs.Task;
        }
    }
}
