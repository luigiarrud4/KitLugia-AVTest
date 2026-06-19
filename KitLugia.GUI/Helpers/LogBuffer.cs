using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace KitLugia.GUI.Helpers
{
    public class LogBuffer
    {
        private readonly ConcurrentQueue<string> _buffer = new();
        private readonly Action<string> _onLine;
        private readonly Dispatcher _dispatcher;
        private readonly int _batchSize;

        public LogBuffer(Action<string> onLine, int batchSize = 50)
        {
            _onLine = onLine;
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _batchSize = batchSize;
        }

        public void OnNext(string message)
        {
            _buffer.Enqueue(message);
            if (_buffer.Count >= _batchSize)
                FlushInternal();
        }

        public void Flush()
        {
            if (_buffer.IsEmpty) return;
            FlushInternal();
        }

        private void FlushInternal()
        {
            var batch = new List<string>(_batchSize);
            while (_buffer.TryDequeue(out var msg))
                batch.Add(msg);

            if (batch.Count == 0) return;

            if (_dispatcher.CheckAccess())
            {
                foreach (var line in batch)
                    _onLine(line);
            }
            else
            {
                _dispatcher.Invoke(() =>
                {
                    foreach (var line in batch)
                        _onLine(line);
                });
            }
        }
    }
}
