// The alarms of the practice plant (examples/mps500): set here, not in its
// NodeSet, which stays the model of the learning factory as it is. Their
// limits lie within the swing of the simulation, so with watched values
// moving at SimulationPace.Watched each alarm comes about every one to two
// minutes and goes again.

using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

public static class PracticePlantAlarms
{
    public const string Mps500Uri = "http://hsu-hh.de/UA/MPS500/";

    /// <summary>The four alarms, their messages through <paramref name="translate"/> (the plugin's language).</summary>
    public static IReadOnlyList<AlarmDefinition> For(Func<string, string>? translate = null)
    {
        string T(string text) => translate?.Invoke(text) ?? text;
        static UaNodeAddress Node(int id) => new(Mps500Uri, UaIdType.Numeric, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return
        [
            // ST30 DV_Drill_spindle/Speed, 1080 in the model, swinging from 864 to 1296.
            new AlarmDefinition("SpindleOverload", Node(1167), AlarmKind.High, 1200, false, 800, T(Messages[0])),
            // ST10 DV_Magazine_empty_sensor/Signal: true is an empty magazine.
            new AlarmDefinition("MagazineEmpty", Node(1066), AlarmKind.State, 0, false, 500, T(Messages[1])),
            // ST30 CTRL_PLC/CpuLoad, 50 in the model, swinging from 40 to 60.
            new AlarmDefinition("PlcLoadHigh", Node(1190), AlarmKind.High, 57, false, 200, T(Messages[2])),
            // ST40 DV_Robot_controller/Online: false is a controller that does not answer.
            new AlarmDefinition("RobotControllerOffline", Node(1219), AlarmKind.State, 0, true, 800, T(Messages[3])),
        ];
    }

    /// <summary>The messages in English, as the plugin looks them up for its language.</summary>
    public static readonly IReadOnlyList<string> Messages =
        ["Drill spindle overloaded", "Magazine empty", "PLC load high", "Robot controller offline"];
}
