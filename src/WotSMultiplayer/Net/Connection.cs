using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace WotSMultiplayer.Net
{
    internal enum NetEventKind { Accepted, Connected, ConnectFailed, Received, Closed }

    /// <summary>Produced on background threads, consumed on the main thread by NetSession.Poll().</summary>
    internal sealed class NetEvent
    {
        public NetEventKind Kind;
        public Connection Conn;
        public TcpClient Client;   // Accepted
        public byte Type;          // Received
        public byte[] Payload;     // Received
        public string Reason;      // ConnectFailed / Closed
    }

    /// <summary>Thread-safe FIFO (.NET 3.5 has no ConcurrentQueue).</summary>
    internal sealed class NetEventQueue
    {
        private readonly Queue<NetEvent> q = new Queue<NetEvent>();
        public void Enqueue(NetEvent e) { lock (q) q.Enqueue(e); }
        public int Count { get { lock (q) return q.Count; } }
        public bool TryDequeue(out NetEvent e)
        {
            lock (q)
            {
                if (q.Count == 0) { e = null; return false; }
                e = q.Dequeue();
                return true;
            }
        }
    }

    /// <summary>
    /// One TCP connection with a reader thread and a writer thread, so the game's main thread never blocks on the
    /// network. Sends are queued; Close(graceful: true) flushes queued frames (e.g. a Bye or a rejection) first.
    /// Hardening (security review 2026-11-25): the first frame (the handshake: Hello / Welcome) may be at most
    /// Protocol.MaxHandshakeFrameBytes, so nobody can make the game reserve big buffers before the room code is checked;
    /// a peer that sends faster than the game handles its messages (backlog over MaxBacklog) is dropped.
    /// </summary>
    internal sealed class Connection
    {
        /// <summary>Messages read but not yet handled by the main thread, per connection, before the peer is dropped.</summary>
        public const int MaxBacklog = 20000;

        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly NetEventQueue sink;
        private readonly Queue<byte[]> sendQueue = new Queue<byte[]>();
        private readonly object sendLock = new object();
        private bool closing;
        private int closedReported; // 0/1, Interlocked
        private int backlog;        // Interlocked: received, not yet handled
        public readonly string RemoteEndPoint;
        public readonly IPAddress RemoteAddress;

        public Connection(TcpClient client, NetEventQueue sink)
        {
            this.client = client;
            this.sink = sink;
            client.NoDelay = true;
            stream = client.GetStream();
            try
            {
                IPEndPoint ep = (IPEndPoint)client.Client.RemoteEndPoint;
                RemoteAddress = ep.Address;
                RemoteEndPoint = ep.ToString();
            }
            catch { RemoteEndPoint = "?"; RemoteAddress = IPAddress.None; }
            new Thread(ReadLoop) { IsBackground = true, Name = "WotSMP-read" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "WotSMP-write" }.Start();
        }

        /// <summary>Main thread: one received message of this connection has been handled.</summary>
        public void Handled() => Interlocked.Decrement(ref backlog);

        private static int rejectsInFlight;

        /// <summary>
        /// Turn a connection away with one short frame (e.g. "session full") without starting reader/writer threads for it:
        /// a thread-pool item writes the frame (2 s limit), half-closes and closes. At most 8 at a time; beyond that the
        /// socket is just closed, so a burst of connections can't tie up threads.
        /// </summary>
        public static void RejectAndClose(TcpClient c, byte type, byte[] payload)
        {
            if (Interlocked.Increment(ref rejectsInFlight) > 8)
            {
                Interlocked.Decrement(ref rejectsInFlight);
                try { c.Close(); } catch { }
                return;
            }
            byte[] frame = Protocol.Frame(type, payload);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    NetworkStream s = c.GetStream();
                    s.WriteTimeout = 2000;
                    s.Write(frame, 0, frame.Length);
                    s.Flush();
                    c.Client.Shutdown(SocketShutdown.Send);
                    Thread.Sleep(1000); // let the frame arrive before the reset (see GracefulShutdown)
                }
                catch { }
                finally
                {
                    try { c.Close(); } catch { }
                    Interlocked.Decrement(ref rejectsInFlight);
                }
            });
        }

        public void Send(byte type, byte[] payload)
        {
            byte[] frame = Protocol.Frame(type, payload);
            lock (sendLock)
            {
                if (closing) return;
                sendQueue.Enqueue(frame);
                Monitor.Pulse(sendLock);
            }
        }

        /// <summary>graceful: deliver already-queued frames, then close. Otherwise close immediately.</summary>
        public void Close(bool graceful)
        {
            lock (sendLock)
            {
                closing = true;
                if (!graceful) sendQueue.Clear();
                Monitor.Pulse(sendLock);
            }
            if (!graceful) Shutdown();
        }

        private void WriteLoop()
        {
            bool drained = false;
            try
            {
                while (true)
                {
                    byte[] frame;
                    lock (sendLock)
                    {
                        while (sendQueue.Count == 0 && !closing) Monitor.Wait(sendLock);
                        if (sendQueue.Count == 0) break; // closing and drained
                        frame = sendQueue.Dequeue();
                    }
                    stream.Write(frame, 0, frame.Length);
                }
                stream.Flush();
                drained = true;
            }
            catch (Exception e)
            {
                Report("send failed: " + e.Message);
            }
            finally
            {
                if (drained) GracefulShutdown(); else Shutdown();
            }
        }

        /// <summary>
        /// Half-close (FIN) so the peer can read our last frames (Bye / rejection), then let the peer hang up;
        /// force-close after 3 s. Closing the socket outright loses those frames on Unity's Mono (it resets the
        /// connection): seen in playtest, where a rejected client got "connection lost" instead of the reason.
        /// </summary>
        private void GracefulShutdown()
        {
            try { client.Client.Shutdown(SocketShutdown.Send); }
            catch { Shutdown(); return; }
            new Thread(() => { Thread.Sleep(3000); Shutdown(); }) { IsBackground = true, Name = "WotSMP-linger" }.Start();
        }

        private void ReadLoop()
        {
            try
            {
                byte[] header = new byte[4];
                bool first = true;
                while (true)
                {
                    if (!ReadExact(header, 4)) { Report("connection closed by the other side"); return; }
                    int len = BitConverter.ToInt32(header, 0);
                    int max = first ? Protocol.MaxHandshakeFrameBytes : Protocol.MaxFrameBytes;
                    if (len < 1 || len > max) { Report("invalid frame length " + len + (first ? " (handshake)" : "")); return; }
                    first = false;
                    byte[] body = new byte[len];
                    if (!ReadExact(body, len)) { Report("connection closed mid-message"); return; }
                    byte[] payload = new byte[len - 1];
                    Buffer.BlockCopy(body, 1, payload, 0, len - 1);
                    if (Interlocked.Increment(ref backlog) > MaxBacklog) { Report("too many messages (flood)"); return; }
                    sink.Enqueue(new NetEvent { Kind = NetEventKind.Received, Conn = this, Type = body[0], Payload = payload });
                }
            }
            catch (Exception e)
            {
                Report(e is IOException || e is SocketException || e is ObjectDisposedException
                    ? "connection lost" : "receive error: " + e.Message);
            }
        }

        private bool ReadExact(byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = stream.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private void Shutdown()
        {
            try { client.Close(); } catch { }
        }

        /// <summary>Raise exactly one Closed event per connection.</summary>
        private void Report(string reason)
        {
            if (Interlocked.Exchange(ref closedReported, 1) == 1) return;
            bool wasClosing;
            lock (sendLock) wasClosing = closing;
            sink.Enqueue(new NetEvent { Kind = NetEventKind.Closed, Conn = this, Reason = wasClosing ? "closed" : reason });
            Shutdown();
        }
    }

    /// <summary>The remote address of an accepted socket, without starting a Connection for it.</summary>
    internal struct TcpClientInfo
    {
        public IPAddress Address;
        public string EndPoint;

        public static TcpClientInfo Of(TcpClient c)
        {
            try
            {
                IPEndPoint ep = (IPEndPoint)c.Client.RemoteEndPoint;
                return new TcpClientInfo { Address = ep.Address, EndPoint = ep.ToString() };
            }
            catch { return new TcpClientInfo { Address = null, EndPoint = "?" }; }
        }
    }

    /// <summary>Accepts TCP clients on a background thread.</summary>
    internal sealed class Listener
    {
        private readonly TcpListener listener;
        private readonly NetEventQueue sink;
        private volatile bool stopped;

        public Listener(IPAddress bind, int port, NetEventQueue sink)
        {
            this.sink = sink;
            listener = new TcpListener(bind, port);
            listener.Start(); // throws SocketException if the port is in use
            new Thread(AcceptLoop) { IsBackground = true, Name = "WotSMP-accept" }.Start();
        }

        private void AcceptLoop()
        {
            while (!stopped)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    if (stopped) { c.Close(); break; }
                    sink.Enqueue(new NetEvent { Kind = NetEventKind.Accepted, Client = c });
                }
                catch
                {
                    if (stopped) break;
                    Thread.Sleep(100);
                }
            }
        }

        public void Stop()
        {
            stopped = true;
            try { listener.Stop(); } catch { }
        }
    }

    /// <summary>Connects on a background thread with a timeout (TcpClient.Connect would block the game).</summary>
    internal static class Connector
    {
        public static void ConnectAsync(string host, int port, int timeoutMs, NetEventQueue sink)
        {
            new Thread(() =>
            {
                TcpClient c = new TcpClient();
                try
                {
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                    {
                        c.Close();
                        sink.Enqueue(new NetEvent { Kind = NetEventKind.ConnectFailed, Reason = "no answer from " + host + ":" + port + " (timed out)" });
                        return;
                    }
                    c.EndConnect(ar);
                    sink.Enqueue(new NetEvent { Kind = NetEventKind.Connected, Client = c });
                }
                catch (Exception e)
                {
                    try { c.Close(); } catch { }
                    sink.Enqueue(new NetEvent { Kind = NetEventKind.ConnectFailed, Reason = "could not connect to " + host + ":" + port + " (" + e.Message + ")" });
                }
            }) { IsBackground = true, Name = "WotSMP-connect" }.Start();
        }
    }
}
