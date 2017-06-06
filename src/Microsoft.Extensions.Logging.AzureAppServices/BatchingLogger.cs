using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.Logging.AzureAppServices
{
    class FileLogger : BatchingLogger
    {
        private readonly string _fileName;

        public FileLogger(string fileName)
        {
            _fileName = fileName;
        }

        protected override async Task WriteMessagesAsync(string message)
        {
            using (var fileStream = File.OpenWrite(_fileName))
            {
                using (var streamWriter = new StreamWriter(fileStream))
                {
                    await streamWriter.WriteAsync(message);
                }
            }
        }
    }

    abstract class BatchingLogger: IDisposable, ILogger
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private readonly TimeSpan _interval;

        private readonly BlockingCollection<string> _messageQueue;
        private readonly Task _outputTask;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly int? _batchSize;

        protected BatchingLogger(TimeSpan interval, int? batchSize, int queueSizeLimit)
        {
            if (queueSizeLimit == 0)
            {
                _messageQueue = new BlockingCollection<string>(new ConcurrentQueue<string>());
            }
            else
            {
                _messageQueue = new BlockingCollection<string>(new ConcurrentQueue<string>(), queueSizeLimit);
            }

            _interval = interval;
            _batchSize = batchSize;
            _cancellationTokenSource = new CancellationTokenSource();
            _messageQueue = new BlockingCollection<string>(new ConcurrentQueue<string>());
            _outputTask = Task.Factory.StartNew(
                ProcessLogQueue,
                null,
                TaskCreationOptions.LongRunning);
        }

        public virtual void EnqueueMessage(string message)
        {
            if (!_messageQueue.IsAddingCompleted)
            {
                try
                {
                    _messageQueue.Add(message);
                }
                catch (InvalidOperationException) { }
            }
        }

        protected abstract Task WriteMessagesAsync(string message);

        private async Task ProcessLogQueue(object state)
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                var limit = _batchSize ?? int.MaxValue;

                while (_messageQueue.TryTake(out var message) && limit > 0)
                {
                    _builder.Append(message);
                    limit--;
                }

                if (_builder.Length > 0)
                {
                    try
                    {
                        await WriteMessagesAsync(_builder.ToString());
                    }
                    catch
                    {
                        // ignored
                    }

                    _builder.Clear();
                }

                await Task.Delay(_interval, _cancellationTokenSource.Token);
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _messageQueue.CompleteAdding();

            try
            {
                _outputTask.Wait(1500); // with timeout in-case Console is locked by user input
            }
            catch (TaskCanceledException) { }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 && ex.InnerExceptions[0] is TaskCanceledException) { }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            // "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message}{NewLine}";
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            builder.Append(" [");
            builder.Append(logLevel.ToString());
            builder.Append("] ");
            builder.AppendLine(formatter(state, exception));

            _messageQueue.Add(builder.ToString());
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }
    }
}
