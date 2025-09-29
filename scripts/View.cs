using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NetFabric.Hyperlinq;
using System.Runtime.InteropServices;
using System.Collections.Immutable;

struct Finger : IComparable<Finger>
{
    public void Initialize(Vector2 pos, int idx)
    {
        Index = idx;
        Position = StartPos = pos;
        MoveTime = PressTime = Time.GetTicksMsec();
    }

    public void Drag(Vector2 newPos)
    {
        MoveTime = Time.GetTicksMsec();
        Position = newPos;

        // if finger moves from slider area to fader area in less than 100ms
        // use it for fader finger
        if (MoveTime - PressTime < 100)
        {
            StartPos = newPos;
        }
    }

    public readonly int CompareTo(Finger other)
    {
        return Index.CompareTo(other.Index);
    }

    public override readonly string ToString()
    {
        return $"Finger {Index} {{ Positon: {Position}, StartPos: {StartPos} }}";
    }

    public static bool operator ==(Finger a, Finger b)
    {
        return a.Index == b.Index;
    }

    public static bool operator !=(Finger a, Finger b)
    {
        return a.Index != b.Index;
    }

    public int Index { get; set; }

    public Vector2 Position { get; set; }
    public Vector2 StartPos { get; set; }

    public ulong PressTime { get; set; }
    public ulong MoveTime { get; set; }

    public override readonly bool Equals(object obj)
    {
        return obj is Finger f && f == this;
    }

    public override readonly int GetHashCode()
    {
        return Index;
    }
}

public partial class View : Node
{
    private readonly ConcurrentDictionary<int, Finger> _fingers = [];

    private List<ColorRect> _buttons;
    private int[] _laneState;
    private Config _config;
    private Window _window;

    private Finger? _leftFaderFinger;
    private Finger? _rightFaderFinger;
    private float _leftFaderAnalog = 0.5f;
    private float _rightFaderAnalog = 0.5f;
    private int _leftFaderDir = 0;
    private int _rightFaderDir = 0;
    private TextureRect _leftFader;
    private TextureRect _rightFader;
    private Label _faderPositionLabel;
    private Label _connectionStateLabel;
    private Label _latencylLabel;
    private ISpiceAPI _connection;
    private static readonly Color COLOR_TOUCH = new Color(1.0f, 1.0f, 1.0f, 0.3f);
    private static readonly Color COLOR_NORMAL = Color.FromHtml("#000");


    private static bool IS_WINDOWS = OS.GetName() == "Windows";

    public override void _Ready()
    {
        _config = Config.EnsureInited();

        if (!_config.LoadSuccess)
        {
            // first open
            GetTree().ChangeSceneToFile("res://Options.tscn");
        }

        _window = GetWindow();

        var buttons = GetNode("UI/Lane/Buttons");
        _buttons = [.. buttons.GetChildren()
            .Where(x => x is ColorRect)
            .Select(x => x as ColorRect)];

        _laneState = new int[_buttons.Count];

        _leftFader = GetNode<TextureRect>("UI/Fader/LeftFader");
        _rightFader = GetNode<TextureRect>("UI/Fader/RightFader");

        _faderPositionLabel = GetNode<Label>("UI/Status/HBoxContainer/FaderPosition");
        _connectionStateLabel = GetNode<Label>("UI/Status/HBoxContainer/ConnectionStatus");
        _latencylLabel = GetNode<Label>("UI/Status/HBoxContainer/Latency");

        var spiceHostLabel = GetNode<Label>("UI/Status/HBoxContainer/SpiceApiAddress");

        _connection = new UdpSpiceAPI(_config.SpiceApiHost, _config.SpiceApiPort, _config.SpiceApiPassword);

        spiceHostLabel.Text = _connection.SpiceHost;

        var faderArea = GetNode<Control>("UI/Fader");
        var laneArea = GetNode<Control>("UI/Lane");

        faderArea.AnchorBottom = _config.FaderAreaSize;
        laneArea.AnchorTop = _config.FaderAreaSize;

        _guard = GetNode<Timer>("ConnectionGuard");
        _guard.Timeout += Guard_Timeout;
        _guard.Start();

        if (_config.DebugTouch)
        {
            _itemList = GetNode<ItemList>("UI/ItemList");
            _itemList.Visible = true;
        }

        _optionsIcon = GetNode<TextureRect>("UI/Status/HBoxContainer/HoldForOptions");
    }

    private void Guard_Timeout()
    {
        _connection.GuardConnection();
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventScreenTouch touch)
        {
            if (touch.Pressed && !touch.Canceled)
            {
                // on windows mouse emulated touch doubletap causes stucked shit
                if (IS_WINDOWS && touch.DoubleTap)
                    return;

                var finger = new Finger();
                // +1 for not use int default value
                finger.Initialize(touch.Position, touch.Index + 1);

                _fingers[touch.Index + 1] = finger;
            }
            else
            {
                _fingers.TryRemove(touch.Index + 1, out var finger);
            }

            _window.SetInputAsHandled();
        }
        else if (e is InputEventScreenDrag drag)
        {
            if (!_fingers.TryGetValue(drag.Index + 1, out var finger))
            {
                finger = new Finger();
                finger.Initialize(drag.Position, drag.Index + 1);
            }
            else
            {
                finger.Drag(drag.Position);
            }

            _fingers[drag.Index + 1] = finger;
            _window.SetInputAsHandled();
        }
    }

    private static (float, bool) UpdateFaderAnalog(int dir, float analog, float frameTime)
    {
        if (dir == 0)
        {
            if (analog == 0.5f)
                return (analog, false);

            analog += (0.5f - analog) * 1.92f;

            if (Mathf.Abs(0.5f - analog) < 0.001f)
            {
                analog = 0.5f;
            }
        }
        else
        {
            var dest = dir > 0 ? 1 : 0;
            if (analog == dest)
            {
                return (analog, false);
            }

            analog += (dest - analog) / 6;

            if (Mathf.Abs(dest - analog) < 0.001f)
            {
                analog = dest;
            }
        }

        return (analog, true);
    }

    private static readonly ulong FIND_OPPOSITE_FADER_DELAY = 500;

    private static Func<Finger, bool> FilterNewFaderFinger(Finger? another, int halfWidth, int halfHeight, Func<float, float, bool> cmpFunc)
    {
        return v =>
        {
            if (v.StartPos.Y > halfHeight)
                return false;

            if (another is null)
            {
                if (!cmpFunc(v.StartPos.X, halfWidth))
                    return false;

                return true;
            }

            if (v.Index == another.Value.Index)
                return false;

            if (!cmpFunc(v.StartPos.X, another.Value.Position.X))
                return false;

            if (v.PressTime - another.Value.PressTime < FIND_OPPOSITE_FADER_DELAY && !cmpFunc(v.StartPos.X, halfWidth))
                return false;

            return true;
        };
    }

    private void UpdateFaderState(ReadOnlySpan<Finger> fingers, float frameTime)
    {
        var halfHeight = (int)(_window.Size.Y * _config.FaderAreaSize);
        var halfWidth = _window.Size.X / 2;

        if (_leftFaderFinger is null)
        {
            var newFinger = fingers.AsValueEnumerable()
                .Where(FilterNewFaderFinger(_rightFaderFinger, halfWidth, halfHeight, (a, b) => a < b))
                .First();

            _leftFaderFinger = newFinger.IsSome ? newFinger.Value : null;
        }
        else
        {
            var newState = fingers.AsValueEnumerable()
                .Where(v => v == _leftFaderFinger)
                .First();

            if (newState.IsSome)
            {
                var delta = newState.Value.Position.X - _leftFaderFinger.Value.Position.X;
                if (MathF.Abs(delta) > _config.FaderDeadZone)
                    _leftFaderDir = Math.Sign(delta);
            }
            else
            {
                _leftFaderDir = 0;
            }

            _leftFaderFinger = newState.IsSome ? newState.Value : null;
        }

        if (_rightFaderFinger is null)
        {
            var newFinger = fingers.AsValueEnumerable()
                .Where(FilterNewFaderFinger(_leftFaderFinger, halfWidth, halfHeight, (a, b) => a > b))
                .First();

            _rightFaderFinger = newFinger.IsSome ? newFinger.Value : null;
        }
        else
        {
            var newState = fingers.AsValueEnumerable()
                .Where(v => v == _rightFaderFinger)
                .First();

            if (newState.IsSome)
            {
                var delta = newState.Value.Position.X - _rightFaderFinger.Value.Position.X;
                if (MathF.Abs(delta) > _config.FaderDeadZone)
                    _rightFaderDir = Math.Sign(delta);
            }
            else
            {
                _rightFaderDir = 0;
            }

            _rightFaderFinger = newState.IsSome ? newState.Value : null;
        }

        var leftUpdated = false;
        (_leftFaderAnalog, leftUpdated) = UpdateFaderAnalog(_leftFaderDir, _leftFaderAnalog, frameTime);

        var rightUpdated = false;
        (_rightFaderAnalog, rightUpdated) = UpdateFaderAnalog(_rightFaderDir, _rightFaderAnalog, frameTime);

        if (leftUpdated || rightUpdated)
        {
            _connection.SendAnalogsState(_leftFaderAnalog, _rightFaderAnalog);
        }
    }

    private void UpdateLaneState(ReadOnlySpan<Finger> fingers)
    {
        Array.Fill(_laneState, 0);

        var halfHeight = _window.Size.Y * _config.FaderAreaSize;
        var laneWidth = _window.Size.X / (float)_laneState.Length;

        for (var i = 0; i < fingers.Length; i++)
        {
            var finger = fingers[i];
            var x = finger.Position.X;

            if (finger.StartPos.Y < halfHeight)
                continue;

            var lane = (int)Math.Clamp(x / laneWidth, 0, _laneState.Length - 1);
            _laneState[lane]++;
        }

        _connection.SendButtonsState(_laneState);
    }

    ulong? optionStartHoldTime = null;
    private Timer _guard;
    private ItemList _itemList;
    private TextureRect _optionsIcon;

    private void DetectOptionHold(ReadOnlySpan<Finger> fingers)
    {
        var range = _optionsIcon.Size.X;
        var start = _optionsIcon.Position.X;

        var isOptionHold = fingers
            .AsValueEnumerable()
            .Any(f =>
                f.Position.X > start && f.Position.X < start + range &&
                f.Position.Y < range
            );

        if (isOptionHold)
        {
            if (optionStartHoldTime is null)
            {
                optionStartHoldTime = Time.GetTicksMsec();
                return;
            }

            var duration = Time.GetTicksMsec() - optionStartHoldTime;

            if (duration > 1000)
            {
                GetTree().ChangeSceneToFile("res://Options.tscn");
            }

            return;
        }

        optionStartHoldTime = null;
    }

    public override void _PhysicsProcess(double frameTime)
    {
        var fingers = _fingers.Values.ToArray();

        if (_config.DebugTouch)
        {
            _itemList.Clear();
            foreach (var finger in fingers)
            {
                _itemList.AddItem(finger.ToString());
            }
        }

        UpdateFaderState(fingers, (float)frameTime);
        UpdateLaneState(fingers);
        DetectOptionHold(fingers);
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            var state = _laneState[i];
            var button = _buttons[i];

            if (state > 0)
                button.Color = COLOR_TOUCH;
            else
                button.Color = COLOR_NORMAL;
        }

        _leftFader.AnchorLeft = _leftFader.AnchorRight = _leftFaderAnalog * 0.5f;
        _rightFader.AnchorLeft = _rightFader.AnchorRight = 0.5f + _rightFaderAnalog * 0.5f;

        _faderPositionLabel.Text = $"{_leftFaderAnalog:F2}, {_rightFaderAnalog:F2}";
        _connectionStateLabel.Text = _connection.Connected ? "CONNECTED" : "DISCONNECTED";
        _latencylLabel.Text = _connection.Connected ? $"{_connection.Latency} ms" : "- ms";
    }

    public override void _ExitTree()
    {
        _guard.Stop();
        _connection.Dispose();
        _connection = null;
    }
}
