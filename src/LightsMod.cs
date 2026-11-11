using System.Reflection;
using System.Text.Json;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KSA.Rendering.Lighting;
using StarMap.API;

namespace AndoyaSpaceport;

/// <summary>
/// Adds the flood lights of the light masts of the site. A StaticObject has no Light element and its fragment shader
/// has no emissive term, so the lamps are dark meshes. The static object draws once per viewport and frame through
/// StaticObject.UpdateRenderData, which gets the matrix of the site frame (X up, Y east, Z north) to the eye frame.
/// A postfix there turns each lamp of lights.json (given in that site frame) into a spot light with
/// ClusteredLightSystem.CreateLightInstance, as LightModule.UpdateRenderData does for a part.
/// The lights fade in when the sun is lower than a few degrees above the horizon at the site.
/// The same postfix gives SiteShadows the site frame, which draws the site into the sun shadow map.
/// </summary>
[StarMapMod]
public sealed class LightsMod
{
    internal const string SiteStaticObject = "AndoyaSpaceport_Prefab_Pad";

    private sealed class Lamp
    {
        public double3 Position;
        public double3 Direction;
        public float Range;
        public float Inner;
        public float Outer;
        public float3 Color;
        public float Intensity;
    }

    private static readonly List<Lamp> Lamps = new();
    private static bool _installed;
    private static bool _shadowsInstalled;
    private static int _logged;

    [StarMapAllModsLoaded]
    public void OnAllModsLoaded()
    {
        try
        {
            Load();
        }
        catch (Exception e)
        {
            Log("[AndoyaSpaceport lights] FAILED to read lights.json, no lamps: " + e);
            Lamps.Clear();
        }
        try
        {
            Install();
        }
        catch (Exception e)
        {
            Log("[AndoyaSpaceport] FAILED to start: " + e);
        }
    }

    [StarMapUnload]
    public void Unload()
    {
    }

    private static void Log(string text)
    {
        try
        {
            string dir = Path.GetDirectoryName(typeof(LightsMod).Assembly.Location) ?? ".";
            File.AppendAllText(Path.Combine(dir, "AndoyaSpaceport.log"), DateTime.Now.ToString("HH:mm:ss ") + text + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static void Load()
    {
        string dir = Path.GetDirectoryName(typeof(LightsMod).Assembly.Location) ?? ".";
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "lights.json")));
        foreach (JsonElement e in doc.RootElement.GetProperty("lamps").EnumerateArray())
        {
            double[] p = e.GetProperty("pos").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            double[] d = e.GetProperty("dir").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            double[] c = e.GetProperty("color").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            Lamps.Add(new Lamp
            {
                Position = new double3(p[0], p[1], p[2]),
                Direction = double3.Normalize(new double3(d[0], d[1], d[2])),
                Range = (float)e.GetProperty("range").GetDouble(),
                Inner = (float)e.GetProperty("inner").GetDouble(),
                Outer = (float)e.GetProperty("outer").GetDouble(),
                Color = new float3((float)c[0], (float)c[1], (float)c[2]),
                Intensity = (float)e.GetProperty("intensity").GetDouble(),
            });
        }
        Log("[AndoyaSpaceport lights] " + Lamps.Count + " lamps loaded.");
    }

    // Every Harmony type is in this method, so a 0Harmony that cannot load fails only here.
    private static void Install()
    {
        MethodInfo target = typeof(StaticObject).GetMethod(nameof(StaticObject.UpdateRenderData))!;
        MethodInfo postfix = typeof(LightsMod).GetMethod(nameof(UpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
        Harmony harmony = new Harmony("AndoyaSpaceport");
        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        _installed = true;
        Log("[AndoyaSpaceport lights] installed.");
        try
        {
            SiteShadows.Install(harmony, Log);
            _shadowsInstalled = true;
        }
        catch (Exception e)
        {
            Log("[AndoyaSpaceport shadows] FAILED to install, no shadows: " + e);
        }
    }

    // Distances in metres from the camera to the site over which the lamps fade out.
    private const double FadeStart = 3000.0;
    private const double FadeEnd = 5000.0;

    // Harmony passes the arguments of StaticObject.UpdateRenderData(IViewport, int, ref readonly double4x4) as __args.
    private static void UpdatePostfix(StaticObject __instance, object[] __args)
    {
        if (!_installed || __instance.Template.Id != SiteStaticObject)
            return;
        try
        {
            IViewport viewport = (IViewport)__args[0];
            double4x4 m = (double4x4)__args[2];
            // The shadows have their own guard, because a fault in their setup must never stop the lamps.
            if (_shadowsInstalled)
            {
                try
                {
                    SiteShadows.NoteSite(viewport, in m);
                }
                catch (Exception e)
                {
                    _shadowsInstalled = false;
                    Log("[AndoyaSpaceport shadows] FAILED, no shadows: " + e);
                }
            }
            if (Lamps.Count == 0)
                return;
            Camera camera = viewport.GetCamera();
            double3 up = new double3(m.M11, m.M12, m.M13).Normalized();
            double factor = 1.0;
            if (Universe.WorldSun != null)
            {
                double3 sun = camera.GetPositionEgo(Universe.WorldSun).Normalized();
                double elevation = Math.Asin(Math.Clamp(double3.Dot(sun, up), -1.0, 1.0)) * 180.0 / Math.PI;
                factor = Math.Clamp((8.0 - elevation) / 8.0, 0.0, 1.0);
            }
            // The camera is at the origin of the ego frame, so the translation of the site matrix is the distance to
            // the site. Far away a lamp covers no pixel, so the lamps fade out between FadeStart and FadeEnd and are
            // not added at all beyond it.
            double distance = new double3(m.M41, m.M42, m.M43).Length();
            factor *= Math.Clamp((FadeEnd - distance) / (FadeEnd - FadeStart), 0.0, 1.0);
            if (_logged++ < 3)
                Log("[AndoyaSpaceport lights] frame: light factor " + factor.ToString("F3") + ", distance " + distance.ToString("F0") + " m");
            if (factor <= 0.0)
                return;
            foreach (Lamp lamp in Lamps)
            {
                double3 pos = lamp.Position.Transform(m);
                double3 d = lamp.Direction;
                double3 dir = new double3(
                    d.X * m.M11 + d.Y * m.M21 + d.Z * m.M31,
                    d.X * m.M12 + d.Y * m.M22 + d.Z * m.M32,
                    d.X * m.M13 + d.Y * m.M23 + d.Z * m.M33).Normalized();
                Light light = Light.CreateSpotLight(pos, dir, lamp.Range, lamp.Outer, lamp.Inner, lamp.Color,
                    (float)(lamp.Intensity * factor), ELightFlags.None);
                Program.LightSystem.CreateLightInstance(light, viewport);
            }
        }
        catch (Exception e)
        {
            if (_logged++ < 6)
                Log("[AndoyaSpaceport lights] light fault: " + e.Message);
        }
    }
}
