﻿using System;
using System.Threading.Tasks;

namespace RoslynPad.UI
{
    public abstract class TelemetryProviderBase : ITelemetryProvider
    {
        private Exception? _lastError;

        public virtual void Initialize(string version, IApplicationSettings settings)
        {
            if (settings.Values.SendErrors)
            {
                var instrumentationKey = GetInstrumentationKey();

                if (!string.IsNullOrEmpty(instrumentationKey))
                {
                    //_client = new TelemetryClient(new TelemetryConfiguration(instrumentationKey));

                    //_client.Context.Component.Version = version;

                    //_client.TrackPageView("Main");
                }
            }

            //if (_client != null)
            {
                AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
                TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;
            }
        }

        protected abstract string? GetInstrumentationKey();

        private void TaskSchedulerOnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            HandleException(args.Exception!.Flatten().InnerException!);
        }

        private void CurrentDomainOnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
        {
            HandleException((Exception)args.ExceptionObject);
            //_client?.Flush();
        }

        protected void HandleException(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return;
            }

            //_client?.TrackException(exception);
            LastError = exception;
        }

        public void ReportError(Exception exception)
        {
            HandleException(exception);
        }

        public Exception? LastError
        {
            get => _lastError;
            private set
            {
                _lastError = value;
                LastErrorChanged?.Invoke();
            }
        }

        public event Action? LastErrorChanged;

        public void ClearLastError()
        {
            LastError = null;
        }
    }
}
