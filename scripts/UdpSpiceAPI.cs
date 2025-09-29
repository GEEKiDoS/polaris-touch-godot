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
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Godot.WebSocketPeer;

class UdpSpiceAPI : ISpiceAPI, IKcpCallback
{
    const int KCP_TIMEOUT_MSEC = 2000;
    const int KCP_POLL_INTERVAL_MSEC = 1;

    private Socket _client;
    private CancellationTokenSource _stopThread = new();
    private SimpleSegManager.Kcp _kcp;
    private RC4 _rc4;

    private readonly IPEndPoint _targetEp;
    private readonly ConcurrentQueue<Func<bool>> _sendTasks = new();
    private readonly Thread _thread;

    private bool _disposed = false;

    private ulong _lastActive = 0;

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
    }

    private void RecreateKcpSession()
    {
        _kcp?.Dispose();
        _client?.Dispose();

        _kcp = null;
        _client = null;

        Connected = false;
        _lastActive = 0;
        
        _client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _client.Blocking = false;
        _client.Bind(new IPEndPoint(IPAddress.Any, 0));

        _kcp = new(573, this);
        // fast mode
        _kcp.NoDelay(1, KCP_POLL_INTERVAL_MSEC, 2, 1);
        _rc4?.Reset();
    }

    void KcpUpdate()
    {
        var now = DateTimeOffset.UtcNow;
        // hack, make it instant update
        _kcp.Update(_kcp.Check(now));
    }

    private bool Recv()
    {
        var recvBuffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            var len = _client.ReceiveFrom(recvBuffer, SocketFlags.None, ref ep);

            _lastActive = Time.GetTicksMsec();
            Connected = true;

            _kcp.Input(recvBuffer[..len]);
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
        finally
        {
            ArrayPool<byte>.Shared.Return(recvBuffer);
        }

        return false;
    }

    private string ReadResponse()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int len = -1;
            for (int retry = 0; retry < 10; retry++)
            {
                if (Recv())
                {
                    KcpUpdate();
                    len = _kcp.Recv(buffer);

                    if (len >= 0)
                        break;
                }

                Thread.Sleep(KCP_POLL_INTERVAL_MSEC);
            }

            if (len > 0)
            {
                _rc4?.Crypt(buffer.AsSpan(0, len));
                return Encoding.UTF8.GetString(buffer, 0, len - 1);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return null;
    }

    void UpdateThread()
    {
        while (!_stopThread.IsCancellationRequested)
        {
            if (Recv())
                KcpUpdate();

            GenerateSendAnalogsTask();

            while (_sendTasks.TryDequeue(out var task))
            {
                // if recv failed, discard send queue
                if (!task())
                    _sendTasks.Clear();
            }

            if (Time.GetTicksMsec() - _lastActive > KCP_TIMEOUT_MSEC || _kcp.WaitSnd > 100)
            {
                if (Connected)
                {
                    GD.Print($"Disconnected from SpiceAPI");
                }

                Thread.Sleep(1000);
                if (!_stopThread.IsCancellationRequested)
                {
                    RecreateKcpSession();
                    _sendTasks.Clear();
                }
            }

            Thread.Sleep(1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Send(string data)
    {
        bool SendAction()
        {
            if (_kcp is null)
                return false;

            var byteLen = Encoding.UTF8.GetByteCount(data);
            Span<byte> bytes = stackalloc byte[byteLen + 1];
            Encoding.ASCII.GetBytes(data, bytes);
            bytes[^1] = 0;

            _rc4?.Crypt(bytes);

            var start = Time.GetTicksMsec();

            for (var result = -2; result == -2;)
            {
                result = _kcp.Send(bytes);
                Thread.Sleep(0);
            }

            // actually send packet
            KcpUpdate();
            var response = ReadResponse();
            if (response != null)
            {
                var dur = Time.GetTicksMsec() - start;
                _latencies.Add((int)dur);

                if (_latencies.Count >= 30)
                {
                    Latency = (int)_latencies.Average();
                    _latencies.Clear();
                }

                return true;
            }

            return false;
        }

        _sendTasks.Enqueue(SendAction);
    }

    public void GuardConnection()
    {
        if (!Connected)
            return;

        // Send empty packet to keep kcp connection alive
        // if connected
        Send("");
    }

    int _lastId = 0;
    string[] packetParamsBuffer = new string[12];

    bool[] _lastButtonState = new bool[12];

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
            var newState = state[i] > 0;
            if (Connected && newState == _lastButtonState[i])
                continue;

            _lastButtonState[i] = newState;
            packetParamsBuffer[paramCount++] = $"[\"Button {i + 1}\",{(newState ? "1" : "0")}]";
        }

        if (paramCount == 0)
            return;

        var paramStr = string.Join(',', packetParamsBuffer.Take(paramCount));
        Send($"{{\"id\":{_lastId++},\"module\":\"buttons\",\"function\":\"write\",\"params\":[{paramStr}]}}");
    }

    float _lastLeftFader = -1;
    float _lastRightFader = -1;
    float _newFaderLeft = 0;
    float _newFaderRight = 0;

    public void SendAnalogsState(float left, float right)
    {
        _newFaderLeft = left;
        _newFaderRight = right;
    }

    private void GenerateSendAnalogsTask()
    {
        int paramCount = 0;
        if (!Connected || _newFaderLeft != _lastLeftFader)
        {
            _lastLeftFader = _newFaderLeft;
            packetParamsBuffer[paramCount++] = $"[\"Fader-L\",{_newFaderLeft:F2}]";
        }

        if (!Connected || _newFaderRight != _lastRightFader)
        {
            _lastRightFader = _newFaderRight;
            packetParamsBuffer[paramCount++] = $"[\"Fader-R\",{_newFaderRight:F2}]";
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

        _sendTasks.Clear();
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
