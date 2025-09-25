using Godot;

class Config
{
    public static Config Instance { get; private set; }

    public string SpiceApiHost { get; set; }
    public ushort SpiceApiPort { get; set; }
    public float FaderAreaSize { get; set; }

    private Config()
    {    
    }

    public static Config EnsureInited()
    {
        if (Instance != null) 
            return Instance;

        Instance = new Config();
        Instance.TryReload();
        return Instance;
    }

    public void Reset()
    {
        SpiceApiHost = "192.168.1.100";
        SpiceApiPort = 1337;
        FaderAreaSize = 0.5f;
    }

    public bool TryReload()
    {
        var config = new ConfigFile();
        var err = config.Load("user://config.cfg");

        if (err != Error.Ok)
        {
            Reset();
            return false;
        }

        SpiceApiHost = config.GetValue("spice_api", "host", "192.168.1.100").As<string>();
        SpiceApiPort = config.GetValue("spice_api", "port", 1337).As<ushort>();
        FaderAreaSize = config.GetValue("controller", "fader_area_size", 0.5f).As<float>();

        return true;
    }

    public void Save()
    {
        var config = new ConfigFile();

        config.SetValue("spice_api", "host", SpiceApiHost);
        config.SetValue("spice_api", "port", SpiceApiPort);
        config.SetValue("controller", "fader_area_size", FaderAreaSize);

        config.Save("user://config.cfg");
    }
}
