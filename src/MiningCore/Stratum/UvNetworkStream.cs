using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using NetUV.Core.Buffers;
using NetUV.Core.Handles;
using NLog;

namespace MiningCore.Stratum
{
    public class UvNetworkStream : Stream
    {
        public UvNetworkStream(Tcp tcp, Loop loop)
        {
            this.tcp = tcp;

            // initialize send queue
            writeQueueDrainer = loop.CreateAsync(DrainSendQueue);
            writeQueueDrainer.UserToken = tcp;

            // cleanup preparation
            var sub = Disposable.Create(() =>
            {
                isAlive = false;

                if (tcp.IsValid)
                {
                    logger.Debug(() => "Shutting down UvNetworkStream Tcp Handle");
                    tcp.Shutdown();
                }
            });

            // ensure subscription is disposed on loop thread
            var disposer = loop.CreateAsync((handle) =>
            {
                sub.Dispose();

                handle.Dispose();
            });

            subscription = Disposable.Create(() => { disposer.Send(); });

            BeginRead();
        }

        private readonly IDisposable subscription;
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
        private readonly Tcp tcp;
        private bool isAlive = true;

        private readonly ConcurrentQueue<ArraySegment<byte>> writeQueue = new ConcurrentQueue<ArraySegment<byte>>();
        private readonly Async writeQueueDrainer;

        private readonly Queue<ReadableBuffer> readQueue = new Queue<ReadableBuffer>();
        private readonly object readLock = new object();
        private Exception readError = null;
        private readonly AutoResetEvent readQueueEvent = new AutoResetEvent(false);

        #region Overrides of Stream

        /// <summary>Releases the unmanaged resources used by the <see cref="T:System.IO.Stream"></see> and optionally releases the managed resources.</summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            subscription?.Dispose();
        }

        /// <summary>Closes the current stream and releases any resources (such as sockets and file handles) associated with the current stream. Instead of calling this method, ensure that the stream is properly disposed.</summary>
        public override void Close()
        {
            Dispose(true);
        }

        /// <summary>When overridden in a derived class, clears all buffers for this stream and causes any buffered data to be written to the underlying device.</summary>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        public override void Flush()
        {
        }

        /// <summary>When overridden in a derived class, reads a sequence of bytes from the current stream and advances the position within the stream by the number of bytes read.</summary>
        /// <param name="buffer">An array of bytes. When this method returns, the buffer contains the specified byte array with the values between offset and (offset + count - 1) replaced by the bytes read from the current source.</param>
        /// <param name="offset">The zero-based byte offset in buffer at which to begin storing the data read from the current stream.</param>
        /// <param name="count">The maximum number of bytes to be read from the current stream.</param>
        /// <returns>The total number of bytes read into the buffer. This can be less than the number of bytes requested if that many bytes are not currently available, or zero (0) if the end of the stream has been reached.</returns>
        /// <exception cref="T:System.ArgumentException">The sum of <paramref name="offset">offset</paramref> and <paramref name="count">count</paramref> is larger than the buffer length.</exception>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="buffer">buffer</paramref> is null.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="offset">offset</paramref> or <paramref name="count">count</paramref> is negative.</exception>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support reading.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadInternal(buffer, offset, count);
        }

        /// <summary>Asynchronously reads a sequence of bytes from the current stream, advances the position within the stream by the number of bytes read, and monitors cancellation requests.</summary>
        /// <param name="buffer">The buffer to write the data into.</param>
        /// <param name="offset">The byte offset in buffer at which to begin writing data from the stream.</param>
        /// <param name="count">The maximum number of bytes to read.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None"></see>.</param>
        /// <returns>A task that represents the asynchronous read operation. The value of the <paramref name="TResult">TResult</paramref> parameter contains the total number of bytes read into the buffer. The result value can be less than the number of bytes requested if the number of bytes currently available is less than the requested number, or it can be 0 (zero) if the end of the stream has been reached.</returns>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="buffer">buffer</paramref> is null.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="offset">offset</paramref> or <paramref name="count">count</paramref> is negative.</exception>
        /// <exception cref="T:System.ArgumentException">The sum of <paramref name="offset">offset</paramref> and <paramref name="count">count</paramref> is larger than the buffer length.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support reading.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The stream has been disposed.</exception>
        /// <exception cref="T:System.InvalidOperationException">The stream is currently in use by a previous read operation.</exception>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadInternalAsync(buffer, offset, count);
        }

        /// <summary>When overridden in a derived class, sets the position within the current stream.</summary>
        /// <param name="offset">A byte offset relative to the origin parameter.</param>
        /// <param name="origin">A value of type <see cref="T:System.IO.SeekOrigin"></see> indicating the reference point used to obtain the new position.</param>
        /// <returns>The new position within the current stream.</returns>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support seeking, such as if the stream is constructed from a pipe or console output.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <summary>When overridden in a derived class, sets the length of the current stream.</summary>
        /// <param name="value">The desired length of the current stream in bytes.</param>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support both writing and seeking, such as if the stream is constructed from a pipe or console output.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>When overridden in a derived class, writes a sequence of bytes to the current stream and advances the current position within this stream by the number of bytes written.</summary>
        /// <param name="buffer">An array of bytes. This method copies count bytes from buffer to the current stream.</param>
        /// <param name="offset">The zero-based byte offset in buffer at which to begin copying bytes to the current stream.</param>
        /// <param name="count">The number of bytes to be written to the current stream.</param>
        /// <exception cref="T:System.ArgumentException">The sum of <paramref name="offset">offset</paramref> and <paramref name="count">count</paramref> is greater than the buffer length.</exception>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="buffer">buffer</paramref> is null.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="offset">offset</paramref> or <paramref name="count">count</paramref> is negative.</exception>
        /// <exception cref="T:System.IO.IOException">An I/O error occured, such as the specified file cannot be found.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support writing.</exception>
        /// <exception cref="T:System.ObjectDisposedException"><see cref="M:System.IO.Stream.Write(System.Byte[],System.Int32,System.Int32)"></see> was called after the stream was closed.</exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnqueueWrite(new ArraySegment<byte>(buffer, offset, count));
        }

        /// <summary>Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream by the number of bytes written, and monitors cancellation requests.</summary>
        /// <param name="buffer">The buffer to write data from.</param>
        /// <param name="offset">The zero-based byte offset in buffer from which to begin copying bytes to the stream.</param>
        /// <param name="count">The maximum number of bytes to write.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None"></see>.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="buffer">buffer</paramref> is null.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="offset">offset</paramref> or <paramref name="count">count</paramref> is negative.</exception>
        /// <exception cref="T:System.ArgumentException">The sum of <paramref name="offset">offset</paramref> and <paramref name="count">count</paramref> is larger than the buffer length.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support writing.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The stream has been disposed.</exception>
        /// <exception cref="T:System.InvalidOperationException">The stream is currently in use by a previous write operation.</exception>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnqueueWrite(new ArraySegment<byte>(buffer, offset, count));
            return Task.FromResult(true);
        }

        /// <summary>When overridden in a derived class, gets a value indicating whether the current stream supports reading.</summary>
        /// <returns>true if the stream supports reading; otherwise, false.</returns>
        public override bool CanRead => true;

        /// <summary>When overridden in a derived class, gets a value indicating whether the current stream supports seeking.</summary>
        /// <returns>true if the stream supports seeking; otherwise, false.</returns>
        public override bool CanSeek => false;

        /// <summary>When overridden in a derived class, gets a value indicating whether the current stream supports writing.</summary>
        /// <returns>true if the stream supports writing; otherwise, false.</returns>
        public override bool CanWrite => true;

        /// <summary>When overridden in a derived class, gets the length in bytes of the stream.</summary>
        /// <returns>A long value representing the length of the stream in bytes.</returns>
        /// <exception cref="T:System.NotSupportedException">A class derived from Stream does not support seeking.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override long Length => throw new NotSupportedException();

        /// <summary>When overridden in a derived class, gets or sets the position within the current stream.</summary>
        /// <returns>The current position within the stream.</returns>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support seeking.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        #endregion

        void BeginRead()
        {
            tcp.OnRead((handle, buffer) =>
            {
                if (buffer.Count == 0 || !isAlive)
                    return;

                buffer.Retain();

                lock (readLock)
                {
                    readQueue.Enqueue(buffer);
                }

                readQueueEvent.Set();
            }, (handle, ex) =>
            {
                // onError
                lock (readLock)
                {
                    readError = ex;
                }

                readQueueEvent.Set();
            }, handle =>
            {
                // onCompleted
                isAlive = false;

                readQueueEvent.Set();

                // release handles
                writeQueueDrainer.UserToken = null;
                writeQueueDrainer.Dispose();

                handle.CloseHandle();

                // clear read queue
                lock (readLock)
                {
                    while (readQueue.TryDequeue(out var buffer))
                        buffer.Dispose();
                }

            });
        }

        private void EnqueueWrite(ArraySegment<byte> buffer)
        {
            writeQueue.Enqueue(buffer);
            writeQueueDrainer.Send();
        }

        private void DrainSendQueue(Async handle)
        {
            try
            {
                var tcp = (Tcp)handle.UserToken;

                if (tcp?.IsValid == true && !tcp.IsClosing && tcp.IsWritable && writeQueue != null)
                {
                    var queueSize = writeQueue.Count;
                    if (queueSize >= 256)
                        logger.Warn(() => $"Write queue backlog now at {queueSize}");

                    while (writeQueue.TryDequeue(out var segment))
                    {
                        tcp.QueueWrite(segment.Array, segment.Offset, segment.Count);
                    }
                }
            }

            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }

        private int ReadInternal(byte[] buffer, int offset, int count)
        {
            while(isAlive)
            {
                lock(readLock)
                {
                    if (readError != null)
                        throw readError;

                    if (readQueue.Count > 0)
                        return ReadInternal2(buffer, offset, count);
                }

                // wait for data
                readQueueEvent.WaitOne();
            }

            return 0;
        }

        private Task<int> ReadInternalAsync(byte[] buffer, int offset, int count)
        {
            // attempt to complete synchronously
            lock (readLock)
            {
                if (readError != null)
                    return Task.FromException<int>(readError);

                if (readQueue.Count > 0)
                    return Task.FromResult(ReadInternal2(buffer, offset, count));
            }

            // run asynchronously
            return Task.Run(() =>
            {
                while (isAlive)
                {
                    lock (readLock)
                    {
                        if (readError != null)
                            throw readError;

                        if (readQueue.Count > 0)
                            return ReadInternal2(buffer, offset, count);
                    }

                    // wait for data
                    readQueueEvent.WaitOne();
                }

                return 0;
            });
        }

        private int ReadInternal2(byte[] buffer, int offset, int count)
        {
            var remaining = count;
            var offsetInternal = 0;
            var tmp = ArrayPool<byte>.Shared.Rent(count);

            try
            {
                while (remaining > 0 && readQueue.Count > 0)
                {
                    var buf = readQueue.Peek();
                    var cb = Math.Min(count, buf.Count);

                    if(offset + offsetInternal == 0)
                        buf.ReadBytes(buffer, cb);
                    else
                    {
                        // NetUv's ReadableBuffer does not support offsets therefore we need a temp buffer 
                        buf.ReadBytes(tmp, cb);
                        Array.Copy(tmp, 0, buffer, offset + offsetInternal, cb);
                    }

                    // done with this buffer
                    if (buf.Count == 0)
                        readQueue.Dequeue().Dispose();

                    remaining -= cb;
                    offsetInternal += cb;
                }

                return count - remaining;
            }

            finally
            {
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }
    }
}
