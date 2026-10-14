#if UNITY_EDITOR
using KotH.Shared.Combat;
using KotH.Shared.Dummy;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using UnityEngine;

/// <summary>
/// Measures the swing: the shape it can actually hit, how far it reaches, how
/// often it lands and where the cap holds. Runs THROUGH SimulationSystem like
/// ImpactProbe, because the combat pass lives there. Throwaway probe.
/// </summary>
public sealed class MeleeProbe : MonoBehaviour
{
    static readonly Vector3 PlayerSpot = new Vector3(0f, 0.9f, -18f);
    const float Yaw = 90f;          // looking along +x

    const float DistanceStep = 0.02f;

    // Derived from the configured reach, so turning the dial up does not
    // silently leave the sweep ending before the limit it is looking for.
    float firstDistance;
    float lastDistance;

    const int SettleTicks = 3;      // after a teleport, before a swing
    const int ReadTicks = 2;        // after a swing, before reading the count
    const int BurstTicks = 131;     // ticks 0..130, so swings at 0,13,...,130

    enum Phase { Hull, Reach, Burst, Sideways, Done }

    PlayerMotor player;
    PlayerMotor dummy;
    Collider dummyHull;   // the shape a ray can actually meet
    TrainingDummy dummyBody;
    DamagePercent damage;
    MeleeAttack attack;
    ScriptedBody scripted;

    Phase phase = Phase.Hull;
    int tick;
    float distance;
    float lastHitCentre = -1f;
    float lastHitRay = -1f;

    void Start()
    {
        if (!ProbeSubject.TryBind(out player))
            return;

        if (!SimulationSystem.HasActive)
        {
            Debug.LogError("[melee] kein SimulationSystem in der Szene");
            UnityEditor.EditorApplication.Exit(3);
            return;
        }

        TrainingDummy target = FindFirstObjectByType<TrainingDummy>();
        if (target == null)
        {
            Debug.LogError("[melee] kein TrainingDummy in der Szene");
            UnityEditor.EditorApplication.Exit(3);
            return;
        }

        dummyBody = target;
        dummy = target.Motor;
        dummyHull = dummy.GetComponent<Collider>();
        damage = target.GetComponent<DamagePercent>();
        attack = player.GetComponent<MeleeAttack>();

        if (damage == null || attack == null)
        {
            Debug.LogError("[melee] DamagePercent am Dummy oder MeleeAttack am Spieler fehlt");
            UnityEditor.EditorApplication.Exit(3);
            return;
        }

        firstDistance = attack.Range - 0.4f;
        lastDistance = attack.Range + 0.8f;

        CapTest();

        scripted = player.gameObject.AddComponent<ScriptedBody>();
        scripted.BeforeTick = Step;

        // The dummy registered during scene load and would otherwise tick
        // before the player, making every reading a tick old.
        dummyBody.enabled = false;
        dummyBody.enabled = true;

        Place(player, PlayerSpot);
        distance = firstDistance;
        PlaceDummy();
    }

    /// <summary>
    /// The cap as plain arithmetic rather than a run of 10 842 ticks. 900
    /// hits at 1.2 % are 1080 %, so the last 67 of them must do nothing.
    /// </summary>
    void CapTest()
    {
        damage.Reset();

        int accepted = 0;
        for (int i = 0; i < 900; i++)
        {
            if (damage.Add(1.2f))
                accepted++;
        }

        Debug.Log($"[melee] == Deckel: nach 900 Schlägen à 1,2 %" +
                  $" steht {damage.Percent:F1} % (soll 1000,0)," +
                  $" angenommen {accepted} von 900 (soll 834)," +
                  $" IsCapped {damage.IsCapped}");

        damage.Reset();
    }

    void Step()
    {
        switch (phase)
        {
            case Phase.Hull: HullStep(); break;
            case Phase.Reach: ReachStep(); break;
            case Phase.Burst: BurstStep(); break;
            case Phase.Sideways: SidewaysStep(); break;
        }
    }

    /// <summary>
    /// Measures the shape a ray can actually meet. A CharacterController is
    /// smaller to a query than its own bounds claim, and the difference sits
    /// exactly where a player aims: at head height.
    /// </summary>
    void HullStep()
    {
        scripted.Input = new MoveInput { Yaw = Yaw };

        tick++;
        if (tick < SettleTicks)
            return;

        Vector3 d = dummy.transform.position;
        Bounds b = dummyHull.bounds;

        // Straight down the axis finds the crown, sideways at the middle
        // finds the radius.
        float crown = float.NaN;
        if (Physics.Raycast(new Vector3(d.x, d.y + 3f, d.z), Vector3.down,
                            out RaycastHit top, 6f, ~0, QueryTriggerInteraction.Ignore))
            crown = top.point.y;

        // From the far side on purpose: the player stands on the -x side and
        // a ray from there measures him instead of the dummy.
        float radius = float.NaN;
        if (Physics.Raycast(new Vector3(d.x + 3f, d.y, d.z), Vector3.left,
                            out RaycastHit side, 6f, ~0, QueryTriggerInteraction.Ignore))
            radius = side.point.x - d.x;

        Debug.Log($"[melee] == Trefferfläche: Dummy-Mitte y {d.y:F3}," +
                  $" bounds oben {b.max.y:F3} unten {b.min.y:F3} breit {b.size.x:F3}," +
                  $" aber abfragbar: Scheitel {crown:F3} Radius {radius:F3}." +
                  $" Oberste {(b.max.y - crown):F3} m sind nicht treffbar," +
                  $" Augenhöhe des Spielers ist {player.EyePosition.y:F3}");

        tick = 0;
        phase = Phase.Reach;
    }

    /// <summary>
    /// Walks the dummy away a step at a time, aiming at its middle, so the
    /// reported limit is measured instead of assumed.
    /// </summary>
    void ReachStep()
    {
        float pitch = AimPitch();
        scripted.Input = new MoveInput { Yaw = Yaw, Pitch = pitch };

        if (tick == SettleTicks)
            scripted.Input = new MoveInput
            {
                Yaw = Yaw,
                Pitch = pitch,
                AttackPresses = 1,
            };

        tick++;
        if (tick < SettleTicks + ReadTicks + 1)
            return;

        if (attack.HitCount > 0)
        {
            lastHitCentre = distance;
            lastHitRay = attack.LastHitDistance;
        }

        distance += DistanceStep;
        tick = 0;
        attack.ResetCounters();

        if (distance <= lastDistance)
        {
            PlaceDummy();
            return;
        }

        Debug.Log($"[melee] == Reichweite: letzter Treffer bei waagerechtem" +
                  $" Abstand {lastHitCentre:F3} m, der Strahl lief dabei" +
                  $" {lastHitRay:F3} m (Grenze {attack.Range:F3})," +
                  $" Schrittweite {DistanceStep:F3} m," +
                  $" Auge auf y {player.EyePosition.y:F3}");

        distance = 2.0f;
        PlaceDummy();
        damage.Reset();
        attack.ResetCounters();
        tick = 0;
        phase = Phase.Burst;
    }

    void BurstStep()
    {
        // Attack input only inside the window, then one quiet tick: the swing
        // of the last tick resolves AFTER this callback, so reading the
        // counters right away lost it every time.
        scripted.Input = new MoveInput
        {
            Yaw = Yaw,
            Pitch = AimPitch(),
            AttackPresses = (byte)(tick < BurstTicks ? 1 : 0),
        };

        tick++;
        if (tick <= BurstTicks)
            return;

        Vector3 d = dummy.transform.position;
        Debug.Log($"[melee] == Takt: {BurstTicks} Ticks Dauerfeuer ergeben" +
                  $" {attack.SwingCount} Schläge und {attack.HitCount} Treffer" +
                  $" (soll je 11), Aufladung {damage.Percent:F1} % (soll 13,2)," +
                  $" Dummy bei ({d.x:F2}/{d.y:F2}/{d.z:F2})");

        // Within reach, but a metre off the line of sight.
        Place(dummy,
              new Vector3(PlayerSpot.x + 1.5f, 0.94f, PlayerSpot.z + 1.0f));

        damage.Reset();
        attack.ResetCounters();
        tick = 0;
        phase = Phase.Sideways;
    }

    void SidewaysStep()
    {
        // Aims dead ahead and level, not at the dummy: the question is
        // whether a ray hits what it is NOT pointed at.
        scripted.Input = new MoveInput { Yaw = Yaw };

        if (tick == SettleTicks)
            scripted.Input = new MoveInput { Yaw = Yaw, AttackPresses = 1 };

        tick++;
        if (tick < SettleTicks + ReadTicks + 1)
            return;

        Vector3 d = dummy.transform.position;
        Debug.Log($"[melee] == Seitlich versetzt: Dummy bei" +
                  $" ({d.x:F2}/{d.y:F2}/{d.z:F2}), Spieler blickt waagerecht" +
                  $" nach +x, Schläge {attack.SwingCount}," +
                  $" Treffer {attack.HitCount} (soll 0)," +
                  $" Aufladung {damage.Percent:F1} % (soll 0,0)");

        phase = Phase.Done;
        MeasureLabel();
        UnityEditor.EditorApplication.Exit(0);
    }

    /// <summary>
    /// Pitch that points the ray at the dummy's middle. Unity counts pitch
    /// positive downwards, which is why the angle is negated.
    /// </summary>
    float AimPitch()
    {
        Vector3 to = dummy.transform.position - player.EyePosition;
        float flat = new Vector2(to.x, to.z).magnitude;
        return -Mathf.Atan2(to.y, flat) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Presence is not function: Unity's default font asks for Arial glyphs,
    /// which Fedora does not have, and the text stays blank with every check
    /// still green. So the font is measured on real text.
    /// </summary>
    void MeasureLabel()
    {
        var label = FindFirstObjectByType<KotH.Client.DamageLabel>();
        if (label == null || label.Label == null)
        {
            Debug.LogError("[melee] kein DamageLabel mit TextMeshPro in der Szene");
            return;
        }

        var text = label.Label;
        Vector2 size = text.GetPreferredValues("1000,0 %");

        Debug.Log($"[melee] == Schrift: \"{text.font?.name}\"," +
                  $" \"1000,0 %\" misst {size.x:F3} x {size.y:F3} Einheiten" +
                  $" (muss über null sein), Schriftgröße {text.fontSize:F2}," +
                  $" aktueller Text \"{text.text}\"," +
                  $" Label auf y {label.transform.position.y:F3}");
    }

    void PlaceDummy()
    {
        Place(dummy,
              new Vector3(PlayerSpot.x + distance, 0.94f, PlayerSpot.z));
    }

    /// <summary>
    /// Teleports a body and clears what it was doing. Velocity survives a
    /// teleport, so without this every case would start with the momentum of
    /// the one before it.
    /// </summary>
    static void Place(PlayerMotor motor, Vector3 at)
    {
        motor.Teleport(at);
        motor.AddVelocity(-motor.Velocity, 0f);
    }
}
#endif
