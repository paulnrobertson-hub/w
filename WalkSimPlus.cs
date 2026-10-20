using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace WalkSimPlus
{
    // credit waffle
    [BepInPlugin("waffle.walksimplus", "WalkSim+", "1.5.0")]
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
        //   M             = toggle mouse look (locks the cursor; crosshair = teleport target)
        //   E (hold)      = grab the surface under the crosshair/mouse and pull yourself to it;
        //                   let go to release (you keep a little momentum)
        static readonly float[] Speeds = { 3f, 5f, 7.5f, 10f };
        static readonly string[] SpeedNames = { "Slow", "Normal", "Fast", "Turbo" };

        const KeyCode WalkKey = KeyCode.F;
        const KeyCode FlyKey = KeyCode.B;
        const KeyCode NoclipKey = KeyCode.N;
        const KeyCode SpeedKey = KeyCode.G;
        const KeyCode TeleportKey = KeyCode.L;
        const KeyCode MouseLookKey = KeyCode.M;
        const KeyCode GrabKey = KeyCode.E;
        const KeyCode SprintKey = KeyCode.LeftShift;
        const KeyCode UpKey = KeyCode.Space;
        const KeyCode DownKey = KeyCode.LeftControl;

        bool walk = false;
        bool fly = false;
        bool noclip = false;
        bool mouseLook = false;
        float yaw, pitch;
        float lookSensitivity = 0.12f;   // degrees per mouse pixel; change to taste
        int speedIndex = 1;
        float sprintMult = 1.6f;
        float accel = 14f;
        float jumpPower = 5.5f;
        float groundCheckDist = 0.35f;
        float teleportMaxDist = 500f;
        float grabReach = 25f;       // how far away a surface can be grabbed
        float grabPullSpeed = 9f;    // how fast the grab pulls you in
        float grabHoldDist = 0.9f;   // stop this far from the grabbed point
        bool grabbing;
        Vector3 grabPoint;

        Vector3 smoothed;
        object playerInstance;
        Component playerComp;
        Rigidbody rb;
        Transform headTf;
        string toast = "";
        float toastUntil;
        bool warnedPlayer;
        bool heartbeatLogged;

        // saved state so we can restore things when toggled off
        bool savedGravity;
        bool gravitySaved;
        FieldInfo layerField;
        object savedLayers;
        bool layersSaved;
        Collider[] playerColliders;
        bool[] savedColliderEnabled;

        bool Flying { get { return fly || noclip; } }
        bool NoGravity { get { return fly || noclip || grabbing; } }

        void Awake()
        {
            Logger.LogInfo("WalkSim+ 1.4.0 awake. Keys: F walk, B fly, N noclip, G speed, L teleport, M mouse look");
            Application.onBeforeRender += ApplyLook;
        }

        // ---------- Mouse look ----------
        void SetMouseLook(bool on)
        {
            mouseLook = on;
            if (on)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    var e = cam.transform.eulerAngles;
                    yaw = e.y;
                    pitch = e.x > 180f ? e.x - 360f : e.x;
                }
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        void ApplyLook()
        {
            if (!mouseLook) return;
            var cam = Camera.main;
            if (cam == null) return;
            cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        void LateUpdate()
        {
            ApplyLook();
        }

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
            if (!warnedPlayer)
            {
                warnedPlayer = true;
                Logger.LogWarning("WalkSim+: could not find the player class (GTPlayer/Player) yet.");
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
            Logger.LogInfo("WalkSim+: " + msg);
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
                    else
                    {
                        Logger.LogWarning("WalkSim+: field locomotionEnabledLayers not found, noclip may not pass through walls.");
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
            if (NoGravity && !gravitySaved)
            {
                savedGravity = rb.useGravity;
                gravitySaved = true;
                rb.useGravity = false;
            }
            else if (!NoGravity && gravitySaved)
            {
                rb.useGravity = savedGravity;
                gravitySaved = false;
            }
        }

        void Update()
        {
            if (!heartbeatLogged && Time.time > 15f)
            {
                heartbeatLogged = true;
                EnsureReady();
                Logger.LogInfo("WalkSim+ running. Input backend: " + Inp.Describe() + ", player found: " + (playerComp != null) + ", camera found: " + (Camera.main != null));
            }

            if (Inp.Down(MouseLookKey))
            {
                SetMouseLook(!mouseLook);
                Toast("Mouse look " + (mouseLook ? "ON" : "OFF"));
            }
            if (mouseLook)
            {
                Vector2 d = Inp.MouseDelta();
                yaw += d.x * lookSensitivity;
                pitch = Mathf.Clamp(pitch - d.y * lookSensitivity, -89f, 89f);
            }

            if (Inp.Down(GrabKey))
            {
                TryGrab();
            }
            if (grabbing && !Inp.Held(GrabKey))
            {
                grabbing = false;
                RefreshGravity();
                Logger.LogInfo("WalkSim+: released grab");
            }

            if (Inp.Down(WalkKey))
            {
                walk = !walk;
                smoothed = Vector3.zero;
                Toast("WalkSim " + (walk ? "ON" : "OFF"));
            }
            if (Inp.Down(FlyKey))
            {
                fly = !fly;
                smoothed = Vector3.zero;
                EnsureReady();
                RefreshGravity();
                Toast("Fly " + (fly ? "ON" : "OFF"));
            }
            if (Inp.Down(NoclipKey))
            {
                noclip = !noclip;
                smoothed = Vector3.zero;
                SetNoclip(noclip);
                RefreshGravity();
                Toast("Noclip " + (noclip ? "ON" : "OFF"));
            }
            if (Inp.Down(SpeedKey))
            {
                speedIndex = (speedIndex + 1) % Speeds.Length;
                Toast("Speed: " + SpeedNames[speedIndex]);
            }
            if (Inp.Down(TeleportKey))
            {
                TeleportToMouse();
            }
        }

        // ---------- Grab ----------
        void TryGrab()
        {
            if (!EnsureReady()) return;
            var cam = Camera.main;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(Inp.MousePos());
            var hits = Physics.RaycastAll(ray, grabReach, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(playerComp.transform)) continue;
                grabPoint = h.point;
                grabbing = true;
                RefreshGravity();
                Logger.LogInfo("WalkSim+: grabbed a surface " + h.distance.ToString("0.0") + "m away");
                return;
            }
            Toast("Grab: nothing in reach");
        }

        // ---------- Teleport ----------
        void TeleportToMouse()
        {
            if (!EnsureReady()) { Toast("Teleport: player not ready"); return; }
            var cam = Camera.main;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(Inp.MousePos());
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
            if (grabbing)
            {
                if (!EnsureReady()) return;
                RefreshGravity();
                Vector3 to = grabPoint - headTf.position;
                float dist = to.magnitude;
                if (dist <= grabHoldDist)
                    rb.velocity = Vector3.zero; // hang on to it
                else
                    rb.velocity = to.normalized * Mathf.Min(grabPullSpeed, (dist - grabHoldDist) * 6f + 1f);
                return;
            }

            if (!walk && !Flying) return;
            if (!EnsureReady()) return;

            RefreshGravity();

            Vector2 stick = Vector2.zero;
            if (Inp.Held(KeyCode.W)) stick.y += 1f;
            if (Inp.Held(KeyCode.S)) stick.y -= 1f;
            if (Inp.Held(KeyCode.D)) stick.x += 1f;
            if (Inp.Held(KeyCode.A)) stick.x -= 1f;
            if (stick.magnitude > 1f) stick.Normalize();

            bool sprint = Inp.Held(SprintKey);
            bool up = Inp.Held(UpKey);
            bool down = Inp.Held(DownKey);
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
            Application.onBeforeRender -= ApplyLook;
            if (mouseLook) SetMouseLook(false);
            if (noclip) SetNoclip(false);
            if (gravitySaved && rb != null) rb.useGravity = savedGravity;
        }

        void OnGUI()
        {
            if (Time.time < toastUntil)
            {
                GUI.Label(new Rect(20, 20, 400, 30), toast);
            }
            if (mouseLook)
            {
                // small crosshair at screen centre
                float cx = Screen.width / 2f, cy = Screen.height / 2f;
                GUI.Label(new Rect(cx - 6, cy - 12, 20, 24), "+");
            }
        }
    }

    // Keyboard/mouse reader. Gorilla Tag may only have Unity's NEW Input System turned on,
    // in which case UnityEngine.Input throws. So try the new system (by reflection, no extra
    // reference needed) and fall back to the old one.
    static class Inp
    {
        static bool init, newOk, legacyBroken, mouseOk;
        static Type keyEnumType;
        static PropertyInfo kbCurrent, kbItem, mouseCurrent, mousePosProp, pDown, pHeld;
        static MethodInfo readVec;
        static string lastBackend = "none";
        static readonly Dictionary<KeyCode, object> keyCache = new Dictionary<KeyCode, object>();

        public static string Describe()
        {
            if (!init) Init();
            return "newInputSystem=" + newOk + ", legacyBroken=" + legacyBroken + ", lastUsed=" + lastBackend;
        }

        static void Init()
        {
            init = true;
            try
            {
                var kbType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                keyEnumType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
                if (kbType != null && keyEnumType != null)
                {
                    kbCurrent = kbType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                    foreach (var p in kbType.GetProperties())
                    {
                        if (p.Name != "Item") continue;
                        var ip = p.GetIndexParameters();
                        if (ip.Length == 1 && ip[0].ParameterType == keyEnumType) { kbItem = p; break; }
                    }
                    newOk = kbCurrent != null && kbItem != null;
                }
            }
            catch { newOk = false; }

            try
            {
                var mType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
                if (mType != null)
                {
                    mouseCurrent = mType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                    mousePosProp = mType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                    if (mousePosProp != null)
                    {
                        foreach (var m in mousePosProp.PropertyType.GetMethods())
                        {
                            if (m.Name == "ReadValue" && m.GetParameters().Length == 0) { readVec = m; break; }
                        }
                    }
                    mouseOk = mouseCurrent != null && mousePosProp != null && readVec != null;
                }
            }
            catch { mouseOk = false; }
        }

        static object GetControl(KeyCode k)
        {
            var kb = kbCurrent.GetValue(null, null);
            if (kb == null) return null;
            object key;
            if (!keyCache.TryGetValue(k, out key))
            {
                string name = k == KeyCode.LeftControl ? "LeftCtrl" : k.ToString();
                key = Enum.Parse(keyEnumType, name);
                keyCache[k] = key;
            }
            return kbItem.GetValue(kb, new object[] { key });
        }

        public static bool Down(KeyCode k)
        {
            if (!init) Init();
            if (newOk)
            {
                try
                {
                    var c = GetControl(k);
                    if (c != null)
                    {
                        if (pDown == null) pDown = c.GetType().GetProperty("wasPressedThisFrame");
                        if (pDown != null)
                        {
                            lastBackend = "InputSystem";
                            if ((bool)pDown.GetValue(c, null)) return true;
                        }
                    }
                }
                catch { }
            }
            if (!legacyBroken)
            {
                try { if (Input.GetKeyDown(k)) { lastBackend = "legacy"; return true; } }
                catch (InvalidOperationException) { legacyBroken = true; }
            }
            return false;
        }

        public static bool Held(KeyCode k)
        {
            if (!init) Init();
            if (newOk)
            {
                try
                {
                    var c = GetControl(k);
                    if (c != null)
                    {
                        if (pHeld == null) pHeld = c.GetType().GetProperty("isPressed");
                        if (pHeld != null && (bool)pHeld.GetValue(c, null)) return true;
                    }
                }
                catch { }
            }
            if (!legacyBroken)
            {
                try { if (Input.GetKey(k)) return true; }
                catch (InvalidOperationException) { legacyBroken = true; }
            }
            return false;
        }

        static PropertyInfo mouseDeltaProp;
        static MethodInfo readDelta;
        static bool deltaInit;

        public static Vector2 MouseDelta()
        {
            if (!init) Init();
            if (!deltaInit)
            {
                deltaInit = true;
                try
                {
                    var mType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
                    if (mType != null)
                    {
                        mouseDeltaProp = mType.GetProperty("delta", BindingFlags.Public | BindingFlags.Instance);
                        if (mouseDeltaProp != null)
                        {
                            foreach (var m in mouseDeltaProp.PropertyType.GetMethods())
                            {
                                if (m.Name == "ReadValue" && m.GetParameters().Length == 0) { readDelta = m; break; }
                            }
                        }
                    }
                }
                catch { }
            }
            if (mouseOk && mouseDeltaProp != null && readDelta != null)
            {
                try
                {
                    var m = mouseCurrent.GetValue(null, null);
                    if (m != null)
                    {
                        var ctrl = mouseDeltaProp.GetValue(m, null);
                        return (Vector2)readDelta.Invoke(ctrl, null);
                    }
                }
                catch { }
            }
            if (!legacyBroken)
            {
                try { return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 8f; }
                catch (Exception) { legacyBroken = true; }
            }
            return Vector2.zero;
        }

        public static Vector2 MousePos()
        {
            if (!init) Init();
            if (mouseOk)
            {
                try
                {
                    var m = mouseCurrent.GetValue(null, null);
                    if (m != null)
                    {
                        var posCtrl = mousePosProp.GetValue(m, null);
                        return (Vector2)readVec.Invoke(posCtrl, null);
                    }
                }
                catch { }
            }
            if (!legacyBroken)
            {
                try { return Input.mousePosition; }
                catch (InvalidOperationException) { legacyBroken = true; }
            }
            return new Vector2(Screen.width / 2f, Screen.height / 2f);
        }
    }
}
