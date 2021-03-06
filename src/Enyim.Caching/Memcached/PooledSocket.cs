//#define DEBUG_IO
using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Enyim.Caching.Memcached
{
	[DebuggerDisplay("[ Address: {endpoint}, IsAlive = {IsAlive} ]")]
    public partial class PooledSocket : IPooledSocket
	{
		private static readonly Enyim.Caching.ILog log = Enyim.Caching.LogManager.GetLogger(typeof(PooledSocket));

		private bool isAlive;
		private Socket socket;
		private IPEndPoint endpoint;

		private BufferedStream inputStream;
		private AsyncSocketHelper helper;
	    private bool _isDisposed = false;

		public PooledSocket(IPEndPoint endpoint, TimeSpan connectionTimeout, TimeSpan receiveTimeout)
		{
			this.isAlive = true;

			var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
			// TODO test if we're better off using nagle
			socket.NoDelay = true;

			var timeout = connectionTimeout == TimeSpan.MaxValue
							? Timeout.Infinite
							: (int)connectionTimeout.TotalMilliseconds;

			var rcv = receiveTimeout == TimeSpan.MaxValue
				? Timeout.Infinite
				: (int)receiveTimeout.TotalMilliseconds;

			socket.ReceiveTimeout = rcv;
			socket.SendTimeout = rcv;
			socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

			ConnectWithTimeout(socket, endpoint, timeout);

			this.socket = socket;
			this.endpoint = endpoint;

			this.inputStream = new BufferedStream(new BasicNetworkStream(socket));
		}

		private static void ConnectWithTimeout(Socket socket, IPEndPoint endpoint, int timeout)
		{
			var mre = new ManualResetEvent(false);

			socket.BeginConnect(endpoint, iar =>
			{
				try { using (iar.AsyncWaitHandle) socket.EndConnect(iar); }
				catch(Exception e){log.Error(e);}

				mre.Set();
			}, null);

			if (!mre.WaitOne(timeout) || !socket.Connected)
				using (socket)
					throw new TimeoutException("Could not connect to " + endpoint);
		}

		public Action<IPooledSocket> CleanupCallback { get; set; }

		public int Available
		{
			get { return this.socket.Available; }
		}

		public void Reset()
		{
			// discard any buffered data
			this.inputStream.Flush();

			if (this.helper != null) this.helper.DiscardBuffer();

			int available = this.socket.Available;

			if (available > 0)
			{
				if (log.IsWarnEnabled)
					log.WarnFormat("Socket bound to {0} has {1} unread data! This is probably a bug in the code. InstanceID was {2}.", this.socket.RemoteEndPoint, available, this.InstanceId);

				byte[] data = new byte[available];

				this.Read(data, 0, available);

				if (log.IsWarnEnabled)
					log.Warn(Encoding.ASCII.GetString(data));
			}

			if (log.IsDebugEnabled)
				log.DebugFormat("Socket {0} was reset", this.InstanceId);
		}

		/// <summary>
		/// The ID of this instance. Used by the <see cref="T:MemcachedServer"/> to identify the instance in its inner lists.
		/// </summary>
		private readonly Guid _instanceId = Guid.NewGuid();
        public Guid InstanceId { get { return _instanceId; }}

		public bool IsAlive
		{
			get { return this.isAlive; }
		}

        /// <summary>
        /// replaces call to Dispose to release instance back into pool
        /// </summary>
	    public void Release()
	    {
            Action<IPooledSocket> releaseSocket = this.CleanupCallback;
            if (releaseSocket != null)
            {
                releaseSocket(this);
            }
	    }

	    public void Dispose()
	    {
	        Dispose(true);
	    }

	    ~PooledSocket()
	    {
	        Dispose(false);
	    }

	    void Dispose(bool disposing)
	    {
	        if (!_isDisposed)
	        {
	            try
	            {
	                if (socket != null)
	                    try
	                    {
	                        this.socket.Close();
	                    }
	                    catch (Exception e)
	                    {
	                        log.Error(e);
	                    }

	                if (this.inputStream != null)
	                    this.inputStream.Dispose();

	                this.inputStream = null;
	                this.socket = null;
	                this.CleanupCallback = null;

                    if (disposing)
                    {
                        GC.SuppressFinalize(this);
                    }
	                _isDisposed = true;
	            }
	            catch (Exception e)
	            {
	                log.Error(e);
	            }
	        }
	    }

		private void CheckDisposed()
		{
			if (this.socket == null)
				throw new ObjectDisposedException("PooledSocket");
		}

		/// <summary>
		/// Reads the next byte from the server's response.
		/// </summary>
		/// <remarks>This method blocks and will not return until the value is read.</remarks>
		public int ReadByte()
		{
			this.CheckDisposed();

			try
			{
				return this.inputStream.ReadByte();
			}
			catch (IOException)
			{
				this.isAlive = false;

				throw;
			}
		}

		/// <summary>
		/// Reads data from the server into the specified buffer.
		/// </summary>
		/// <param name="buffer">An array of <see cref="T:System.Byte"/> that is the storage location for the received data.</param>
		/// <param name="offset">The location in buffer to store the received data.</param>
		/// <param name="count">The number of bytes to read.</param>
		/// <remarks>This method blocks and will not return until the specified amount of bytes are read.</remarks>
		public void Read(byte[] buffer, int offset, int count)
		{
			this.CheckDisposed();

			int read = 0;
			int shouldRead = count;

			while (read < count)
			{
				try
				{
					int currentRead = this.inputStream.Read(buffer, offset, shouldRead);
					if (currentRead < 1)
						continue;

					read += currentRead;
					offset += currentRead;
					shouldRead -= currentRead;
				}
				catch (IOException)
				{
					this.isAlive = false;
					throw;
				}
			}
		}

		public void Write(byte[] data, int offset, int length)
		{
			this.CheckDisposed();

			SocketError status;

			this.socket.Send(data, offset, length, SocketFlags.None, out status);

			if (status != SocketError.Success)
			{
				this.isAlive = false;

				ThrowHelper.ThrowSocketWriteError(this.endpoint, status);
			}
		}

		public void Write(IList<ArraySegment<byte>> buffers)
		{
			this.CheckDisposed();

			SocketError status;

#if DEBUG
			int total = 0;
			for (int i = 0, C = buffers.Count; i < C; i++)
				total += buffers[i].Count;

			if (this.socket.Send(buffers, SocketFlags.None, out status) != total)
				System.Diagnostics.Debugger.Break();
#else
			this.socket.Send(buffers, SocketFlags.None, out status);
#endif

			if (status != SocketError.Success)
			{
				this.isAlive = false;

				ThrowHelper.ThrowSocketWriteError(this.endpoint, status);
			}
		}

		/// <summary>
		/// Receives data asynchronously. Returns true if the IO is pending. Returns false if the socket already failed or the data was available in the buffer.
		/// p.Next will only be called if the call completes asynchronously.
		/// </summary>
		public bool ReceiveAsync(AsyncIOArgs p)
		{
			this.CheckDisposed();

			if (!this.IsAlive)
			{
				p.Fail = true;
				p.Result = null;

				return false;
			}

			if (this.helper == null)
				this.helper = new AsyncSocketHelper(this);

			return this.helper.Read(p);
		}

        Action<IPooledSocket> IPooledSocket.CleanupCallback
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        Guid IPooledSocket.InstanceId
        {
            get { throw new NotImplementedException(); }
        }
    }
}

#region [ License information          ]
/* ************************************************************
 * 
 *    Copyright (c) 2010 Attila Kisk�, enyim.com
 *    
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *    
 *        http://www.apache.org/licenses/LICENSE-2.0
 *    
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *    
 * ************************************************************/
#endregion
