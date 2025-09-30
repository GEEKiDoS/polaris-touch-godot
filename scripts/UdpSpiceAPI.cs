using Godot;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Sockets.Kcp;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class RentedBuffer : IDisposable
{
    public byte[] Buffer { get; private set; }
    public int Size { get; private set; }
    public Span<byte> Span => Buffer.AsSpan(0, Size);

    public RentedBuffer(int size)
    {
        Size = size;
        Buffer = ArrayPool<byte>.Shared.Rent(size);
    }

    void IDisposable.Dispose()
    {
        ArrayPool<byte>.Shared.Return(Buffer);
    }
}

class UdpSpiceAPI : ISpiceAPI, IKcpCallback
{
    const int KCP_TIMEOUT_MSEC = 2000;

    private Socket _client;
    private CancellationTokenSource _stopThread = new();
    private object _sessionLock = new();
    private SimpleSegManager.Kcp _kcp;
    private RC4 _rc4;

    private readonly IPEndPoint _targetEp;
    private readonly ConcurrentQueue<string> _pendingOutputs = new();
    private readonly ConcurrentQueue<TaskCompletionSource<RentedBuffer>> _recvQueue = new();
    private readonly Thread _thread;

    private bool _disposed = false;

    public bool Connected { get; private set; }
    public string SpiceHost { get; private set; }
    public int Latency { get; private set; }

    private List<int> _latencies = new(50);

    public UdpSpiceAPI(string host, ushort port, string password = "")
    {
        if (!string.IsNullOrEmpty(password))
            _rc4 = new RC4(password);

        var ip = IPAddress.Parse(host);
        GD.Print($"parsed ip: {ip}");

        _targetEp = new IPEndPoint(ip, port);

        SpiceHost = $"{_targetEp}";

        RecreateKcpSession();

        _thread = new Thread(UpdateThread);
        _thread.Start();

        RunSendTasks();
    }

    private void RecreateKcpSession()
    {
        _pendingOutputs.Clear();

        lock (_sessionLock)
        {
            while (_recvQueue.TryDequeue(out var tcs))
                tcs.SetCanceled();

            _recvQueue.Clear();

            _kcp?.Dispose();
            _client?.Dispose();

            _kcp = null;
            _client = null;

            Connected = false;

            _client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _client.Blocking = false;
            _client.Bind(new IPEndPoint(IPAddress.Any, 0));

            _kcp = new(573, this);
            // fast mode
            _kcp.NoDelay(1, 0, 2, 1);
            _rc4?.Reset();
        }
    }

    private bool RawRecv()
    {
        using var recvBuffer = new RentedBuffer(4096);

        try
        {
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            var len = _client.ReceiveFrom(recvBuffer.Span, SocketFlags.None, ref ep);

            _kcp.Input(recvBuffer.Span[..len]);
            return true;
        }
        catch (Exception ex)
        {
            if (ex is not SocketException
                {
                    SocketErrorCode: SocketError.ConnectionReset or SocketError.WouldBlock
                })
            {
                GD.PrintErr($"failed to recv kcp message ({ex.GetType().Name}): {ex.Message}");
            }
        }

        return false;
    }

    private void KcpUpdate()
    {
        var now = DateTimeOffset.UtcNow;
        if (_kcp.Check(now) >= now)
            _kcp.Update(now);
    }

    private void KcpRecv()
    {
        if (_recvQueue.Count == 0)
            return;

        var (result, len) = _kcp.TryRecv();
        if (len <= 0)
            return;

        var buffer = new RentedBuffer(len);
        result.Memory.Span.CopyTo(buffer.Span);

        if (_recvQueue.TryDequeue(out var source))
            source.SetResult(buffer);
    }

    void UpdateThread()
    {
        while (_stopThread?.IsCancellationRequested == false)
        {
            lock (_sessionLock)
            {
                RawRecv();
                KcpUpdate();
                KcpRecv();
            }
        }
    }

    async ValueTask<bool> SendAsync(string data)
    {
        var byteLen = Encoding.UTF8.GetByteCount(data);
        using var buffer = new RentedBuffer(byteLen + 1);

        Encoding.ASCII.GetBytes(data, buffer.Span);
        buffer.Span[byteLen] = 0;

        _rc4?.Crypt(buffer.Span);

        var start = Time.GetTicksMsec();
        _kcp.Send(buffer.Span);

        var taskSource = new TaskCompletionSource<RentedBuffer>();
        _recvQueue.Enqueue(taskSource);

        var completedTask = await Task.WhenAny(taskSource.Task, Task.Delay(KCP_TIMEOUT_MSEC));
        if (completedTask != taskSource.Task)
            return false;

        if (taskSource.Task.IsCanceled)
            return false;

        using var response = taskSource.Task.Result;
        if (response == null)
            return false;

        _rc4?.Crypt(response.Span);
        var str = Encoding.UTF8.GetString(response.Span);

        GD.Print(str);

        var dur = Time.GetTicksMsec() - start;
        _latencies.Add((int)dur);

        if (_latencies.Count >= 30)
        {
            Latency = (int)_latencies.Average();
            _latencies.Clear();
        }

        return true;
    }

    private async void RunSendTasks()
    {
        while (_stopThread?.IsCancellationRequested == false)
        {
            // remove old packets after disconnect
            while (_pendingOutputs.Count > 10)
                _pendingOutputs.TryDequeue(out _);

            if (!_pendingOutputs.TryDequeue(out var data))
            {
                await Task.Delay(1);
                continue;
            }

            try
            {
                var success = await SendAsync(data);
                if (!success)
                {
                    if (Connected)
                    {
                        GD.Print($"Disconnected from SpiceAPI");
                        Connected = false;
                    }

                    RecreateKcpSession();
                }
                else
                {
                    Connected = true;
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    void Send(string data)
    {
        _pendingOutputs.Enqueue(data);
    }

    public void GuardConnection()
    {
        if (!Connected)
            return;

        Send("");
    }

    int _lastId = 0;
    string[] packetParamsBuffer = new string[12];
    bool[] _oldStates = new bool[12];

    public void SendButtonsState(ReadOnlySpan<int> state)
    {
        if (state.Length > 12)
        {
            GD.PrintErr("state count is bigger than 12, 何意味");
            return;
        }

        int paramCount = 0;
        for (int i = 0; i < state.Length; i++)
        {
            var on = state[i] > 0;
            if (!Connected || _oldStates[i] != on)
                packetParamsBuffer[paramCount++] = $"[\"Button {i + 1}\",{(on ? "1" : "0")}]";

            _oldStates[i] = on;
        }

        if (paramCount == 0)
            return;

        var paramStr = string.Join(',', packetParamsBuffer.Take(paramCount));
        Send($"{{\"id\":{_lastId++},\"module\":\"buttons\",\"function\":\"write\",\"params\":[{paramStr}]}}");
    }

    float _lastLeftFader = -1;
    float _lastRightFader = -1;

    public void SendAnalogsState(float left, float right)
    {
        int paramCount = 0;
        if (!Connected || MathF.Abs(left - _lastLeftFader) > 0.01f)
        {
            _lastLeftFader = left;
            packetParamsBuffer[paramCount++] = $"[\"Fader-L\",{left:F2}]";
        }

        if (!Connected || MathF.Abs(right - _lastRightFader) > 0.01f)
        {
            _lastRightFader = right;
            packetParamsBuffer[paramCount++] = $"[\"Fader-R\",{right:F2}]";
        }

        if (paramCount == 0)
            return;

        var paramStr = string.Join(',', packetParamsBuffer.Take(paramCount));
        Send($"{{\"id\":{_lastId++},\"module\":\"analogs\",\"function\":\"write\",\"params\":[{paramStr}]}}");
    }

    public void Dispose()
    {
        if (_disposed) return;

        _stopThread.Cancel();
        _thread.Join();

        _pendingOutputs.Clear();
        _stopThread.Dispose();
        _stopThread = null;

        _kcp.Dispose();
        _client.Dispose();
    }

    public void Output(IMemoryOwner<byte> buffer, int len)
    {
        try
        {
            _client.SendTo(buffer.Memory.Span[..len], _targetEp);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"failed to send kcp output message to {_targetEp} ({ex.GetType().Name}): {ex.Message}");
        }
    }
}
