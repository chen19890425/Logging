using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    abstract class BatchingLogger: IDisposable
    {
        StringBuilder _builder = new StringBuilder();
        private TimeSpan _interval;

        BlockingCollection<string> _messageQueue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 1000);
        private Task _outputTask;
        private CancellationTokenSource _cancellationTokenSource;

        public BatchingLogger()
        {
            _cancellationTokenSource = new CancellationTokenSource();
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
                while (_messageQueue.TryTake(out var message))
                {
                    _builder.Append(message);
                }

                if (_builder.Length > 0)
                {
                    await WriteMessagesAsync(_builder.ToString());
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
    }
}
