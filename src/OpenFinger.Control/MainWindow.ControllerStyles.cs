using System.IO;
using System.Text.Json.Nodes;

namespace OpenFinger.Control;

public partial class MainWindow
{
    private ControllerStyleConfigState _controllerStyle = new();

    private static string SharedCoreConfigPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppContext.BaseDirectory;
            }

            return Path.Combine(root, "OpenFinger", "openfinger_config.json");
        }
    }

    private void LoadControllerStylesFromSharedConfig()
    {
        _controllerStyle = new ControllerStyleConfigState();

        try
        {
            if (!File.Exists(SharedCoreConfigPath))
            {
                return;
            }

            var root = JsonNode.Parse(File.ReadAllText(SharedCoreConfigPath))?.AsObject();
            var steamVr = root?["steamvr"]?.AsObject();
            var left = LoadControllerStyleNode(steamVr?["left_style"]?.AsObject());
            var right = LoadControllerStyleNode(steamVr?["right_style"]?.AsObject());
            _controllerStyle = SelectUnifiedStyle(left, right);
        }
        catch
        {
        }
    }

    private static ControllerStyleConfigState LoadControllerStyleNode(JsonObject? node)
    {
        var state = new ControllerStyleConfigState();
        if (node is null)
        {
            return state;
        }

        state.StyleId = ControllerStyleCatalog.Normalize(node["style_id"]?.GetValue<string>());
        state.DisplayName = node["display_name"]?.GetValue<string>() ?? string.Empty;
        state.ControllerTypeOverride = node["controller_type_override"]?.GetValue<string>() ?? string.Empty;
        state.RenderModelOverride = node["render_model_override"]?.GetValue<string>() ?? string.Empty;
        ApplyControllerStylePresetDefaults(state, overwriteCustomFields: false);
        return state;
    }

    private static ControllerStyleConfigState SelectUnifiedStyle(ControllerStyleConfigState left, ControllerStyleConfigState right)
    {
        var leftIsDefault = string.Equals(left.StyleId, ControllerStyleCatalog.Knuckles, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(left.DisplayName)
            && string.IsNullOrWhiteSpace(left.ControllerTypeOverride)
            && string.IsNullOrWhiteSpace(left.RenderModelOverride);
        var rightIsDefault = string.Equals(right.StyleId, ControllerStyleCatalog.Knuckles, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(right.DisplayName)
            && string.IsNullOrWhiteSpace(right.ControllerTypeOverride)
            && string.IsNullOrWhiteSpace(right.RenderModelOverride);

        if (!leftIsDefault)
        {
            return left;
        }

        if (!rightIsDefault)
        {
            return right;
        }

        return left;
    }

    private void SaveControllerStylesToSharedConfig()
    {
        JsonObject root;
        try
        {
            if (File.Exists(SharedCoreConfigPath))
            {
                root = JsonNode.Parse(File.ReadAllText(SharedCoreConfigPath))?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }
        }
        catch
        {
            root = new JsonObject();
        }

        var steamVr = root["steamvr"] as JsonObject ?? new JsonObject();
        root["steamvr"] = steamVr;
        var node = BuildControllerStyleNode(_controllerStyle);
        steamVr["left_style"] = node.DeepClone();
        steamVr["right_style"] = node.DeepClone();

        Directory.CreateDirectory(Path.GetDirectoryName(SharedCoreConfigPath)!);
        File.WriteAllText(SharedCoreConfigPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject BuildControllerStyleNode(ControllerStyleConfigState state)
    {
        return new JsonObject
        {
            ["style_id"] = ControllerStyleCatalog.Normalize(state.StyleId),
            ["display_name"] = state.DisplayName ?? string.Empty,
            ["controller_type_override"] = state.ControllerTypeOverride ?? string.Empty,
            ["render_model_override"] = state.RenderModelOverride ?? string.Empty
        };
    }

    public void SetControllerStylePreset(string styleId)
    {
        _controllerStyle.StyleId = ControllerStyleCatalog.Normalize(styleId);
        ApplyControllerStylePresetDefaults(_controllerStyle, overwriteCustomFields: true);
        SaveControllerStylesToSharedConfig();
        RefreshUiFromState();
        SetPinnedStatusLine($"控制器样式已改为 {ControllerStyleCatalog.Get(_controllerStyle.StyleId).Label}。重启 SteamVR 后生效。", 5);
    }

    private static void ApplyControllerStylePresetDefaults(ControllerStyleConfigState state, bool overwriteCustomFields)
    {
        var definition = ControllerStyleCatalog.Get(state.StyleId);
        if (overwriteCustomFields || string.IsNullOrWhiteSpace(state.ControllerTypeOverride))
        {
            state.ControllerTypeOverride = definition.ControllerType;
        }

        if (overwriteCustomFields || string.IsNullOrWhiteSpace(state.RenderModelOverride))
        {
            state.RenderModelOverride = definition.RenderModel;
        }

        if (overwriteCustomFields || string.IsNullOrWhiteSpace(state.DisplayName))
        {
            state.DisplayName = definition.Label;
        }
    }

    private ControllerStyleDashboardState BuildControllerStyleDashboardState()
    {
        var definition = ControllerStyleCatalog.Get(_controllerStyle.StyleId);
        return new ControllerStyleDashboardState
        {
            StyleId = _controllerStyle.StyleId,
            Label = definition.Label,
            PreviewText = $"左右手会统一显示成 {definition.Label} 样式。修改后重启 SteamVR 生效。"
        };
    }
}
