#if UNITY_EDITOR
using KotH.Shared.Dummy;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using UnityEngine;

/// <summary>
/// Measures the push between two bodies. Unlike the other probes it does not
/// tick the motor itself: the push is computed inside SimulationSystem, so a
/// probe that went around it would measure nothing at all. Throwaway probe.
/// </summary>
public sealed class ImpactProbe : MonoBehaviour
{
    struct Case
    {
        public string name;
        public Vector3 start;   // player transform position, not its feet
        public float yaw;
        public Vector2 move;
        public bool sprint;
        public bool crouch;
        public int ticks;
        public int settle;      // pushes before this tick are not counted
        public int trace;       // log every n-th tick, 0 switches it off
    }

    // Far from every other measurement: StepTest sits around z -10, CrouchTest
    // within z +-1.5, the mountain beyond z 10. The ground reaches z -25.
    static readonly Vector3 DummySpot = new Vector3(0f, 0.9f, -18f);

    static readonly Case[] Cases =
    {
        new Case { name = "Sprint frontal", start = new Vector3(-6f, 0.9f, -18f),
                   yaw = 90f, move = new Vector2(0f, 1f), sprint = true, ticks = 70 },
        new Case { name = "Gehen frontal", start = new Vector3(-4f, 0.9f, -18f),
                   yaw = 90f, move = new Vector2(0f, 1f), ticks = 70 },
        new Case { name = "Schleichen frontal", start = new Vector3(-2f, 0.9f, -18f),
                   yaw = 90f, move = new Vector2(0f, 1f), crouch = true, ticks = 70 },
        new Case { name = "Schräg versetzt", start = new Vector3(-4f, 0.9f, -18.5f),
                   yaw = 90f, move = new Vector2(0f, 1f), ticks = 70 },
        new Case { name = "Von oben drauf", start = new Vector3(0f, 4f, -18f),
                   yaw = 90f, ticks = 70 },
        new Case { name = "Ruhig draufstehen", start = new Vector3(0f, 2.74f, -18f),
                   yaw = 90f, ticks = 100, settle = 20 },
        new Case { name = "Dauerdruck", start = new Vector3(-2f, 0.9f, -18f),
                   yaw = 90f, move = new Vector2(0f, 1f), ticks = 150, trace = 10 },
    };

    PlayerMotor player;
    CharacterController playerController;
    PlayerMotor dummy;
    CharacterController dummyController;
    TrainingDummy dummyBody;
    ScriptedBody scripted;

    int caseIndex = -1;
    int tick;
    int pushes;
    bool reported;

    void Start()
    {
        if (!ProbeSubject.TryBind(out player, out playerController))
            return;

        if (!SimulationSystem.HasActive)
        {
            Debug.LogError("[impact] kein SimulationSystem in der Szene");
            UnityEditor.EditorApplication.Exit(3);
            return;
        }

        TrainingDummy target = FindFirstObjectByType<TrainingDummy>();
        if (target == null)
        {
            Debug.LogError("[impact] kein TrainingDummy in der Szene");
            UnityEditor.EditorApplication.Exit(3);
            return;
        }

        dummy = target.Motor;
        dummyController = dummy.GetComponent<CharacterController>();
        dummyBody = target;

        // The input reader is off by now, so this is the only body on the
        // player and the motor is still ticked exactly once.
        scripted = player.gameObject.AddComponent<ScriptedBody>();
        scripted.BeforeTick = Step;

        // The dummy registered during scene load, so it would tick BEFORE
        // the player and its velocity would already be a tick old when read
        // below. Re-registering moves it behind the player.
        dummyBody.enabled = false;
        dummyBody.enabled = true;

        Next();
    }

    /// <summary>
    /// Runs inside the system's FixedUpdate, after the contacts and before
    /// the tick. Any push is therefore already in the velocities here, which
    /// is what makes it separable from ordinary acceleration.
    /// </summary>
    void Step()
    {
        if (caseIndex >= Cases.Length)
            return;

        Case c = Cases[caseIndex];

        if (SimulationSystem.LastContactCount > 0 && tick >= c.settle)
        {
            pushes++;

            if (!reported)
            {
                reported = true;

                Vector3 n = SimulationSystem.LastNormal;
                float approach = SimulationSystem.LastApproach;

                float alongPlayer = Vector3.Dot(player.Velocity, n);
                float alongDummy = Vector3.Dot(dummy.Velocity, n);

                // Separation must be 2 * bounce * approach, and the sum along
                // the normal must survive the hit - that is the momentum.
                Debug.Log($"[impact] {c.name}: erster Stoß bei Tick {tick}," +
                          $" Normale {Show(n)}, Annäherung {approach:F3} m/s," +
                          $" Spieler entlang n {alongPlayer:F3}," +
                          $" Dummy entlang n {alongDummy:F3}," +
                          $" Trennung {alongDummy - alongPlayer:F3}" +
                          $" (soll {2f / 3f * approach:F3})," +
                          $" Summe entlang n {alongPlayer + alongDummy:F3}," +
                          $" Spieler {Show(player.Velocity)}, Dummy {Show(dummy.Velocity)}");
            }
        }

        if (c.trace > 0 && tick % c.trace == 0)
            Debug.Log($"[impact] {c.name} Tick {tick}:" +
                      $" Spieler x {player.transform.position.x:F3}" +
                      $" v {Show(player.Velocity)}," +
                      $" Dummy x {dummy.transform.position.x:F3}" +
                      $" v {Show(dummy.Velocity)}," +
                      $" Stöße in diesem Tick {SimulationSystem.LastContactCount}");

        scripted.Input = new MoveInput
        {
            Move = c.move,
            Yaw = c.yaw,
            Sprint = c.sprint,
            Crouch = c.crouch,
        };

        tick++;
        if (tick < c.ticks)
            return;

        Report(c);
        Next();
    }

    void Next()
    {
        caseIndex++;
        if (caseIndex >= Cases.Length)
        {
            UnityEditor.EditorApplication.Exit(0);
            return;
        }

        Case c = Cases[caseIndex];

        Place(playerController, player, c.start);
        Place(dummyController, dummy, DummySpot);

        tick = 0;
        pushes = 0;
        reported = false;

        scripted.Input = new MoveInput { Yaw = c.yaw };
    }

    void Report(Case c)
    {
        Vector3 p = player.transform.position;
        Vector3 d = dummy.transform.position;
        Vector3 gap = d - p;

        Debug.Log($"[impact] == {c.name}: Stöße {pushes}," +
                  $" Spieler ({p.x:F2}/{p.y:F2}/{p.z:F2})," +
                  $" Dummy ({d.x:F2}/{d.y:F2}/{d.z:F2})," +
                  $" waagerecht {new Vector2(gap.x, gap.z).magnitude:F3} m," +
                  $" senkrecht {gap.y:F3} m");
    }

    /// <summary>
    /// Teleports a body and clears what it was doing. Velocity survives a
    /// teleport, so without this every case would start with the momentum of
    /// the one before it.
    /// </summary>
    static void Place(CharacterController cc, PlayerMotor motor, Vector3 at)
    {
        cc.enabled = false;
        motor.transform.position = at;
        cc.enabled = true;

        motor.AddVelocity(-motor.Velocity, 0f);
    }

    static string Show(Vector3 v) => $"({v.x:F2}/{v.y:F2}/{v.z:F2})";
}
#endif
