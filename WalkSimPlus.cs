using System;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace WalkSimPlus
{
    // credit waffle
    [BepInPlugin("waffle.walksimplus", "WalkSim+", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        // Keyboard controls:
        //   W / A / S / D = move (relative to where you look)
        //   Left Shift    = sprint
        //   Space         = jump (walk) / go up (fly)
        //   Left Ctrl     = go down (fly)
        //   F             = toggle WalkSim on/off
        //   B             = toggle Fly on/off
        //   N             = toggle Noclip on/off (also turns Fly on while active)
        //   G             = cycle speed preset
        //   L             = teleport to where your mouse is pointing
        static readonly float[] Speeds = { 3f, 5f, 7.5f, 10f };
        static readonly string[] SpeedNames = { "Slow", "Normal", "Fast", "Turbo" };

        const KeyCode WalkKey = KeyCode.F;
        const KeyCode FlyKey = KeyCode.B;
        const KeyCode NoclipKey = KeyCode.N;
        const KeyCode SpeedKey = KeyCode.G;
        const KeyCode TeleportKey = KeyCode.L;
        const KeyCode SprintKey = KeyCode.LeftShift;
        const KeyCode UpKey = KeyCode.Space;
        const KeyCode DownKey = KeyCode.LeftControl;

        bool walk = false;
        bool fly = false;
        bool noclip = false;
        int speedIndex = 1;
        float sprintMult = 1.6f;
        float accel = 14f;
        float jumpPower = 5.5f;
        float groundCheckDist = 0.35f;
        float teleportMaxDist = 500f;

        Vector3 smoothed;
        object playerInstance;
        Component playerComp;
        Rigidbody rb;
        Transform headTf;
        string toast = "";
        float toastUntil;

        // saved state so we can restore things when toggled off
        bool savedGravity;
        bool gravitySaved;
        FieldInfo layerField;
        object savedLayers;
        bool layersSaved;
        Collider[] playerColliders;
        bool[] savedColliderEnabled;

        bool Flying { get { return fly || noclip; } }

        void Start()
        {
            ResolvePlayer();
        }

        void ResolvePlayer()
        {
            // Class name differs between Gorilla Tag versions, so look for both.
            string[] names =
            {
                "GorillaLocomotion.GTPlayer, Assembly-CSharp",
                "GorillaLocomotion.Player, Assembly-CSharp"
            };
            foreach (var n in names)
            {
                var t = Type.GetType(n);
                if (t == null) continue;
                var prop = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var field = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                var inst = prop != null ? prop.GetValue(null, null) : (field != null ? field.GetValue(null) : null);
                var c = inst as Component;
                if (c != null)
                {
                    playerInstance = inst;
                    playerComp = c;
                    rb = c.GetComponent<Rigidbody>();
                    return;
                }
            }
        }

        bool EnsureReady()
        {
            if (rb == null || playerComp == null) ResolvePlayer();
            if (Camera.main != null) headTf = Camera.main.transform;
            return rb != null && headTf != null && playerComp != null;
        }

        void Toast(string msg)
        {
            toast = msg;
            toastUntil = Time.time + 2f;
        }

        // ---------- Noclip ----------
        // Gorilla Tag does its own collision with sphere casts against a layer mask,
        // so we blank that mask (and disable the body colliders) while noclip is on.
        void SetNoclip(bool on)
        {
            if (!EnsureReady()) return;
            var t = playerInstance.GetType();

            if (on)
            {
                if (!layersSaved)
                {
                    layerField = t.GetField("locomotionEnabledLayers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (layerField != null)
                    {
                        savedLayers = layerField.GetValue(playerInstance);
                        layerField.SetValue(playerInstance, (LayerMask)0);
                        layersSaved = true;
                    }
                }
                if (playerColliders == null)
                {
                    playerColliders = playerComp.GetComponentsInChildren<Collider>(true);
                    savedColliderEnabled = new bool[playerColliders.Length];
                    for (int i = 0; i < playerColliders.Length; i++)
                    {
                        savedColliderEnabled[i] = playerColliders[i].enabled;
                        playerColliders[i].enabled = false;
                    }
                }
            }
            else
            {
                if (layersSaved && layerField != null)
                {
                    layerField.SetValue(playerInstance, savedLayers);
                    layersSaved = false;
                }
                if (playerColliders != null)
                {
                    for (int i = 0; i < playerColliders.Length; i++)
                    {
                        if (playerColliders[i] != null) playerColliders[i].enabled = savedColliderEnabled[i];
                    }
                    playerColliders = null;
                    savedColliderEnabled = null;
                }
            }
        }

        // ---------- Fly gravity handling ----------
        void RefreshGravity()
        {
            if (rb == null) return;
            if (Flying && !gravitySaved)
            {
                savedGravity = rb.useGravity;
                gravitySaved = true;
                rb.useGravity = false;
            }
            else if (!Flying && gravitySaved)
            {
                rb.useGravity = savedGravity;
                gravitySaved = false;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(WalkKey))
            {
                walk = !walk;
                smoothed = Vector3.zero;
                Toast("WalkSim " + (walk ? "ON" : "OFF"));
            }
            if (Input.GetKeyDown(FlyKey))
            {
                fly = !fly;
                smoothed = Vector3.zero;
                RefreshGravity();
                Toast("Fly " + (fly ? "ON" : "OFF"));
            }
            if (Input.GetKeyDown(NoclipKey))
            {
                noclip = !noclip;
                smoothed = Vector3.zero;
                SetNoclip(noclip);
                RefreshGravity();
                Toast("Noclip " + (noclip ? "ON" : "OFF"));
            }
            if (Input.GetKeyDown(SpeedKey))
            {
                speedIndex = (speedIndex + 1) % Speeds.Length;
                Toast("Speed: " + SpeedNames[speedIndex]);
            }
            if (Input.GetKeyDown(TeleportKey))
            {
                TeleportToMouse();
            }
        }

        // ---------- Teleport ----------
        void TeleportToMouse()
        {
            if (!EnsureReady()) return;
            var cam = Camera.main;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            var hits = Physics.RaycastAll(ray, teleportMaxDist, ~0, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0)
            {
                Toast("Teleport: nothing under mouse");
                return;
            }
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            RaycastHit? best = null;
            foreach (var h in hits)
            {
                // skip our own player's colliders
                if (h.collider.transform.IsChildOf(playerComp.transform)) continue;
                best = h;
                break;
            }
            if (!best.HasValue)
            {
                Toast("Teleport: nothing under mouse");
                return;
            }

            Vector3 dest = best.Value.point + best.Value.normal * 0.6f;
            DoTeleport(dest);
            Toast("Teleported");
        }

        void DoTeleport(Vector3 dest)
        {
            // Prefer the game's own TeleportTo if it exists, so internal position
            // tracking stays in sync. Fall back to moving the rigidbody directly.
            var t = playerInstance.GetType();
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != "TeleportTo") continue;
                var ps = m.GetParameters();
                if (ps.Length < 2) continue;
                if (ps[0].ParameterType != typeof(Vector3) || ps[1].ParameterType != typeof(Quaternion)) continue;

                var args = new object[ps.Length];
                args[0] = dest;
                args[1] = playerComp.transform.rotation;
                for (int i = 2; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                try
                {
                    m.Invoke(playerInstance, args);
                    rb.velocity = Vector3.zero;
                    return;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("TeleportTo failed, falling back: " + e.Message);
                }
            }

            rb.velocity = Vector3.zero;
            rb.position = dest;
            playerComp.transform.position = dest;
            smoothed = Vector3.zero;
        }

        // ---------- Movement ----------
        void FixedUpdate()
        {
            if (!walk && !Flying) return;
            if (!EnsureReady()) return;

            RefreshGravity();

            Vector2 stick = Vector2.zero;
            if (Input.GetKey(KeyCode.W)) stick.y += 1f;
            if (Input.GetKey(KeyCode.S)) stick.y -= 1f;
            if (Input.GetKey(KeyCode.D)) stick.x += 1f;
            if (Input.GetKey(KeyCode.A)) stick.x -= 1f;
            if (stick.magnitude > 1f) stick.Normalize();

            bool sprint = Input.GetKey(SprintKey);
            bool up = Input.GetKey(UpKey);
            bool down = Input.GetKey(DownKey);
            float speed = Speeds[speedIndex] * (sprint ? sprintMult : 1f);

            if (Flying)
            {
                // Full 3D movement along the camera direction
                Vector3 dir = headTf.forward * stick.y + headTf.right * stick.x;
                if (up) dir += Vector3.up;
                if (down) dir -= Vector3.up;
                if (dir.magnitude > 1f) dir.Normalize();

                Vector3 target = dir * speed;
                smoothed = Vector3.Lerp(smoothed, target, 1f - Mathf.Exp(-accel * Time.fixedDeltaTime));
                rb.velocity = smoothed;
                return;
            }

            // Ground walk
            Vector3 fwd = Vector3.ProjectOnPlane(headTf.forward, Vector3.up).normalized;
            Vector3 rgt = Vector3.ProjectOnPlane(headTf.right, Vector3.up).normalized;
            Vector3 wdir = fwd * stick.y + rgt * stick.x;
            if (wdir.magnitude > 1f) wdir.Normalize();

            Vector3 wtarget = wdir * speed;
            smoothed = Vector3.Lerp(smoothed, wtarget, 1f - Mathf.Exp(-accel * Time.fixedDeltaTime));

            Vector3 v = rb.velocity;
            Vector3 horiz = new Vector3(v.x, 0f, v.z);
            Vector3 newHoriz = Vector3.Lerp(horiz, smoothed, 0.85f);
            rb.velocity = new Vector3(newHoriz.x, v.y, newHoriz.z);

            if (up && IsGrounded())
            {
                rb.velocity = new Vector3(rb.velocity.x, jumpPower, rb.velocity.z);
            }
        }

        bool IsGrounded()
        {
            var origin = rb.position + Vector3.up * 0.1f;
            return Physics.Raycast(origin, Vector3.down, groundCheckDist + 0.1f, ~0, QueryTriggerInteraction.Ignore);
        }

        void OnDestroy()
        {
            // put everything back if the plugin unloads
            if (noclip) SetNoclip(false);
            if (gravitySaved && rb != null) rb.useGravity = savedGravity;
        }

        void OnGUI()
        {
            if (Time.time < toastUntil)
            {
                GUI.Label(new Rect(20, 20, 400, 30), toast);
            }
        }
    }
}
