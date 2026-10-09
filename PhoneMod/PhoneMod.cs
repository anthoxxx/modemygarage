// PhoneMod for My Garage - Samsung S23 style phone
// P (or Esc / power button) = open/close
// Apps: Parts, Sell car, Bitcoin, Calculator, Wallpaper, Mod (noclip / money / quick save), Vehicles
//
// Built against the game's Assembly-CSharp.dll. No other mod is required
// (no SimplePartLoader dependency).
// The look is drawn in code (rounded textures generated at runtime), no asset needed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace PhoneMod
{
    public class PhoneModMain : Mod
    {
        public override string ID => "PhoneMod";
        public override string Name => "Phone";
        public override string Author => "You";
        public override string Version => "2.0";

        GameObject host;

        public override void OnLoad()
        {
            if (host == null)
            {
                host = new GameObject("PhoneMod_Host");
                host.AddComponent<PhoneUI>();
            }
        }
    }

    public class PhoneUI : MonoBehaviour
    {
        enum Page { Closed, Home, Shop, Sell, Wallpaper, Bitcoin, Calc, Cheats, Vehicles }
        enum SellState { Idle, Waiting, Offer }

        // ---- settings ----
        const KeyCode OpenKey = KeyCode.P;   // <- change this line to use another key
        const float OfferDelay = 15f;
        const float MaxCarDistance = 10f;
        const float SellRatio = 0.6f;
        const float MinOffer = 500f;
        const float BtcMin = 5000f;        // bitcoin price range
        const float BtcMax = 15000f;
        const float BtcEvery = 60f;        // new price every 60 s

        // ---- phone size (logical units, scaled to screen height) ----
        const float PW = 340f, PH = 736f; // 19.5:9 like the S23

        // ---- colors ----
        static readonly Color Frame   = new Color32(74, 75, 82, 255);
        static readonly Color Body    = new Color32(11, 11, 13, 255);
        static readonly Color AppBg   = new Color32(18, 18, 20, 255);
        static readonly Color Card    = new Color32(31, 31, 36, 255);
        static readonly Color Field   = new Color32(38, 38, 43, 255);
        static readonly Color Blue    = new Color32(59, 130, 246, 255);
        static readonly Color BlueHov = new Color32(96, 165, 250, 255);
        static readonly Color Green   = new Color32(34, 197, 94, 255);
        static readonly Color GreyBtn = new Color32(58, 58, 64, 255);
        static readonly Color Muted   = new Color32(160, 160, 170, 255);

        Page page = Page.Closed;

        // shop
        class ShopItem { public string Name; public float Price; public Partinfo Info; }
        List<ShopItem> shop;
        string search = "";
        Vector2 scroll;
        GameObject spawnSpot;
        bool typing;
        bool pausedByPhone;

        // sell
        SellState sellState = SellState.Idle;
        GameObject targetCar;
        float offerAt;
        float offerValue;

        string toastTitle = "", toastText = "";
        float toastUntil;

        // bitcoin
        float btcPrice, btcPrev, btcHeld, nextBtcAt;
        readonly List<float> btcHist = new List<float>();

        // vehicle spawner
        class CarItem { public string Name; public GameObject Prefab; }
        List<CarItem> cars;
        string carSearch = "";
        Vector2 carScroll;
        bool carStock;   // false = junkyard-style car (like ModUtils emulator), true = complete stock car

        // mod app (noclip / money / quick save)
        bool noclip;
        Transform ncPlayer;
        Rigidbody ncRb;
        bool ncGravity, ncKinematic;
        readonly List<Collider> ncCols = new List<Collider>();
        string lastSave = "";

        // calculator
        string calcDisp = "0", calcExpr = "";
        double calcAcc;
        string calcOp;
        bool calcFresh = true;

        // wallpapers
        class WallItem
        {
            public string Name;
            public Color Top, Bottom;   // gradient wallpapers
            public byte[] Data;         // image wallpapers built into the DLL
            public string File;         // optional extra images from the PhoneWallpapers folder
            public Texture2D Tex;       // baked, rounded to the screen shape
            public bool LightBg;        // true = use dark text on top
        }
        List<WallItem> walls;
        int wallIdx;
        Vector2 wallScroll;

        // game references (found once, cached)
        GameObject player;
        FirstPersonAIO playerAIO;
        Component cashSource;
        object cashClip;
        MethodInfo playOneShot;

        bool FindPlayer()
        {
            if (player != null && playerAIO != null) return true;
            player = GameObject.Find("Player");
            if (player == null) return false;
            playerAIO = player.GetComponent<FirstPersonAIO>();

            // cash sound = the same one the game plays in shops (hand -> AudioManager.Cash)
            GameObject hand = GameObject.Find("hand");
            if (hand != null)
            {
                AudioManager am = hand.GetComponent<AudioManager>();
                cashSource = hand.GetComponent("AudioSource");
                if (am != null && cashSource != null)
                {
                    FieldInfo f = typeof(AudioManager).GetField("Cash");
                    cashClip = f != null ? f.GetValue(am) : null;
                    if (cashClip != null)
                        playOneShot = cashSource.GetType().GetMethod("PlayOneShot", new Type[] { cashClip.GetType() });
                }
            }
            return playerAIO != null;
        }

        void PlayCashSound()
        {
            try { if (playOneShot != null && cashSource != null) playOneShot.Invoke(cashSource, new object[] { cashClip }); } catch { }
        }

        // true when the player is sitting in a car (same check as SimplePartLoader)
        bool PlayerInCar()
        {
            return FindPlayer() && player.transform.parent != null &&
                   player.transform.root.GetComponent<MainCarProperties>() != null;
        }

        // every buyable car part of the game (same list SimplePartLoader builds)
        static List<GameObject> GetGameParts()
        {
            List<GameObject> parts = new List<GameObject>();
            GameObject pp = GameObject.Find("PartsParent");
            JunkPartsList jpl = pp != null ? pp.GetComponent<JunkPartsList>() : null;
            if (jpl != null && jpl.Parts != null)
            {
                foreach (GameObject part in jpl.Parts)
                {
                    if (part == null) continue;
                    if (part.tag != "Item") parts.Add(part);
                    else
                    {
                        PickupTool pt = part.GetComponent<PickupTool>();
                        if (pt != null && pt.Box && pt.InBoxprefab != null) parts.Add(pt.InBoxprefab);
                    }
                }
            }
            GameObject shopItems = GameObject.Find("SHOPITEMS");
            if (shopItems != null)
            {
                foreach (SaleItem si in shopItems.GetComponentsInChildren<SaleItem>(true))
                    if (si.Item != null && si.Item.GetComponent<CarProperties>() != null && !parts.Contains(si.Item))
                        parts.Add(si.Item);
            }
            return parts;
        }

        // Texture2D.LoadImage lives in UnityEngine.ImageConversionModule: called by reflection
        static MethodInfo loadImage;
        static bool LoadImage(Texture2D t, byte[] data)
        {
            if (loadImage == null)
            {
                Type ic = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                if (ic == null) return false;
                loadImage = ic.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
                if (loadImage == null) return false;
            }
            return (bool)loadImage.Invoke(null, new object[] { t, data });
        }

        // gui helpers
        static readonly Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();
        GUIStyle lbl, btn, field;

        // ------------------------------------------------------------ wallpapers
        void Awake()
        {
            btcHeld = PlayerPrefs.GetFloat("PhoneMod_BTC", 0f);
            btcPrice = btcPrev = Mathf.Round(UnityEngine.Random.Range(BtcMin, BtcMax));
            btcHist.Add(btcPrice);
            nextBtcAt = Time.time + BtcEvery;

            walls = new List<WallItem>
            {
                new WallItem { Name = "Night",      Top = new Color32(30, 48, 90, 255),  Bottom = new Color32(8, 12, 26, 255) },
                new WallItem { Name = "Sunset",     Top = new Color32(255, 128, 64, 255), Bottom = new Color32(84, 28, 112, 255) },
                new WallItem { Name = "Forest",     Top = new Color32(40, 140, 100, 255), Bottom = new Color32(6, 36, 38, 255) },
                new WallItem { Name = "Racing red", Top = new Color32(210, 36, 44, 255),  Bottom = new Color32(28, 0, 6, 255) },
                new WallItem { Name = "Carbon",     Top = new Color32(66, 66, 72, 255),   Bottom = new Color32(12, 12, 14, 255) },
            };

            // the two logos are built into the DLL, nothing to copy
            walls.Add(new WallItem { Name = "Top mecanic", Data = Convert.FromBase64String(EmbeddedWalls.TopMecanic) });
            walls.Add(new WallItem { Name = "My garage",   Data = Convert.FromBase64String(EmbeddedWalls.MyGarage) });

            // optional: any .jpg/.png in a PhoneWallpapers folder becomes a wallpaper too
            string dir = FindWallpaperDir();
            if (dir != null)
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                    walls.Add(new WallItem { Name = Path.GetFileNameWithoutExtension(f).Replace('_', ' '), File = f });
                }
            }

            wallIdx = Mathf.Clamp(PlayerPrefs.GetInt("PhoneMod_WP", 0), 0, walls.Count - 1);
        }

        static string FindWallpaperDir()
        {
            List<string> c = new List<string>();
            try { c.Add(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "PhoneWallpapers")); } catch { }
            c.Add(Path.Combine(Application.dataPath, "../Mods/PhoneWallpapers"));
            c.Add(Path.Combine(Application.dataPath, "../PhoneWallpapers"));
            foreach (string d in c)
            {
                try { if (Directory.Exists(d)) return d; } catch { }
            }
            return null;
        }

        Texture2D GetWall(WallItem wi)
        {
            if (wi.Tex != null) return wi.Tex;

            const int tw = 330, th = 726, rad = 33;   // same size as the phone screen
            Texture2D src = null;
            if (wi.Data != null || wi.File != null)
            {
                try
                {
                    src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!LoadImage(src, wi.Data != null ? wi.Data : File.ReadAllBytes(wi.File))) { Destroy(src); src = null; }
                }
                catch { src = null; }
            }

            Color edge = src != null ? src.GetPixel(0, 0) : Color.black;
            wi.LightBg = src != null && (0.299f * edge.r + 0.587f * edge.g + 0.114f * edge.b) > 0.6f;

            float sc = 1f;
            if (src != null) sc = Mathf.Min((float)tw / src.width, (float)th / src.height) * 0.9f; // fit, centered
            float sw = src != null ? src.width * sc : 1f, sh = src != null ? src.height * sc : 1f;

            Texture2D t = new Texture2D(tw, th, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            Color[] px = new Color[tw * th];
            for (int y = 0; y < th; y++)
            {
                for (int x = 0; x < tw; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float cx = Mathf.Clamp(fx, rad, tw - rad), cy = Mathf.Clamp(fy, rad, th - rad);
                    float d = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    float a = Mathf.Clamp01(rad - d + 0.5f);

                    Color c;
                    if (src == null)
                    {
                        c = Color.Lerp(wi.Bottom, wi.Top, fy / th);   // y = 0 is the bottom
                    }
                    else
                    {
                        float u = (fx - (tw - sw) / 2f) / sw, v = (fy - (th - sh) / 2f) / sh;
                        c = (u < 0f || u > 1f || v < 0f || v > 1f) ? edge : src.GetPixelBilinear(u, v);
                    }
                    c.a = a;
                    px[y * tw + x] = c;
                }
            }
            t.SetPixels(px);
            t.Apply();
            wi.Tex = t;
            if (src != null) Destroy(src);
            return t;
        }

        // ------------------------------------------------------------ logic
        void Update()
        {
            // noclip: free flight (WASD + Space up + Ctrl down, Shift = fast)
            if (noclip)
            {
                if (ncPlayer == null) { noclip = false; ncCols.Clear(); }
                else if (page == Page.Closed && Camera.main != null)
                {
                    Transform cam = Camera.main.transform;
                    Vector3 d = Vector3.zero;
                    if (Input.GetKey(KeyCode.W)) d += cam.forward;
                    if (Input.GetKey(KeyCode.S)) d -= cam.forward;
                    if (Input.GetKey(KeyCode.D)) d += cam.right;
                    if (Input.GetKey(KeyCode.A)) d -= cam.right;
                    if (Input.GetKey(KeyCode.Space)) d += Vector3.up;
                    if (Input.GetKey(KeyCode.LeftControl)) d -= Vector3.up;
                    if (d.sqrMagnitude > 0f)
                    {
                        float speed = Input.GetKey(KeyCode.LeftShift) ? 18f : 6f;
                        ncPlayer.position += d.normalized * speed * Time.deltaTime;
                    }
                }
            }

            // new bitcoin price every 60 s, even with the phone closed
            if (Time.time >= nextBtcAt)
            {
                btcPrev = btcPrice;
                btcPrice = Mathf.Round(UnityEngine.Random.Range(BtcMin, BtcMax));
                btcHist.Add(btcPrice);
                if (btcHist.Count > 12) btcHist.RemoveAt(0);
                nextBtcAt = Time.time + BtcEvery;
                if (btcHeld > 0f) Toast("Bitcoin", "BTC is now $" + btcPrice.ToString("N0"), 4f);
            }

            if (Input.GetKeyDown(OpenKey) && !typing)
            {
                if (page == Page.Closed) OpenPhone(); else ClosePhone();
            }
            else if (page != Page.Closed && Input.GetKeyDown(KeyCode.Escape))
            {
                ClosePhone();
            }

            // the 15 s timer keeps running even with the phone closed
            if (sellState == SellState.Waiting && Time.time >= offerAt)
            {
                if (targetCar == null) { sellState = SellState.Idle; return; }
                offerValue = ComputeOffer(targetCar);
                sellState = SellState.Offer;
                Toast("Sell car", "New offer: $" + offerValue.ToString("N0") + "  (press " + OpenKey + ")", 6f);
            }
        }

        void OpenPhone()
        {
            if (!FindPlayer()) return;
            page = Page.Home;
            pausedByPhone = !playerAIO.controllerPauseState;
            if (pausedByPhone) playerAIO.ControllerPause();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void ClosePhone()
        {
            page = Page.Closed;
            typing = false;
            GUI.FocusControl(null);
            if (!pausedByPhone) return; // another menu was already open: leave it as it was
            pausedByPhone = false;
            if (playerAIO != null && playerAIO.controllerPauseState) playerAIO.ControllerUnPause();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // ------------------------------------------------------------ drawing helpers
        static Texture2D RoundTex(int w, int h, int r, Color c)
        {
            string key = w + "x" + h + "r" + r + ColorUtility.ToHtmlStringRGBA(c);
            Texture2D t;
            if (texCache.TryGetValue(key, out t) && t != null) return t;

            t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            Color[] px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float px_ = x + 0.5f, py_ = y + 0.5f;
                    float cx = Mathf.Clamp(px_, r, w - r);
                    float cy = Mathf.Clamp(py_, r, h - r);
                    float d = Mathf.Sqrt((px_ - cx) * (px_ - cx) + (py_ - cy) * (py_ - cy));
                    float a = Mathf.Clamp01(r - d + 0.5f);
                    px[y * w + x] = new Color(c.r, c.g, c.b, c.a * a);
                }
            }
            t.SetPixels(px);
            t.Apply();
            texCache[key] = t;
            return t;
        }

        static void Round(Rect rc, float radius, Color c)
        {
            int w = Mathf.Max(2, Mathf.RoundToInt(rc.width * 2));
            int h = Mathf.Max(2, Mathf.RoundToInt(rc.height * 2));
            int r = Mathf.Min(Mathf.RoundToInt(radius * 2), Mathf.Min(w, h) / 2);
            GUI.DrawTexture(rc, RoundTex(w, h, r, c));
        }

        static Texture2D Slice(Color c) { return RoundTex(64, 64, 20, c); }

        void Text(Rect rc, string s, int size, Color col, TextAnchor anchor, FontStyle fs = FontStyle.Normal)
        {
            lbl.fontSize = size;
            lbl.normal.textColor = col;
            lbl.alignment = anchor;
            lbl.fontStyle = fs;
            GUI.Label(rc, s, lbl);
        }

        bool Button(Rect rc, string s, Color n, Color h, int size = 14)
        {
            btn.normal.background = Slice(n);
            btn.hover.background = Slice(h);
            btn.active.background = Slice(h);
            btn.fontSize = size;
            return GUI.Button(rc, s, btn);
        }

        void InitStyles()
        {
            if (lbl != null) return;

            lbl = new GUIStyle(GUI.skin.label);
            lbl.wordWrap = false;
            lbl.clipping = TextClipping.Overflow;

            btn = new GUIStyle(GUI.skin.button);
            btn.border = new RectOffset(20, 20, 20, 20);
            btn.alignment = TextAnchor.MiddleCenter;
            btn.fontStyle = FontStyle.Bold;
            btn.normal.textColor = btn.hover.textColor = btn.active.textColor = Color.white;

            field = new GUIStyle(GUI.skin.textField);
            field.border = new RectOffset(20, 20, 20, 20);
            field.padding = new RectOffset(16, 16, 0, 0);
            field.fontSize = 14;
            field.alignment = TextAnchor.MiddleLeft;
            Texture2D f = Slice(Field);
            field.normal.background = f; field.hover.background = f;
            field.focused.background = f; field.active.background = f;
            field.normal.textColor = field.hover.textColor =
                field.focused.textColor = field.active.textColor = Color.white;
        }

        // ------------------------------------------------------------ OnGUI
        void OnGUI()
        {
            InitStyles();

            float s = UnityEngine.Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float w = UnityEngine.Screen.width / s;
            float h = UnityEngine.Screen.height / s;

            // notification banner (works with the phone closed too)
            if (Time.time < toastUntil)
            {
                Rect nb = new Rect(w / 2f - 190f, 16f, 380f, 60f);
                Round(nb, 18f, new Color(0.12f, 0.12f, 0.14f, 0.96f));
                Text(new Rect(nb.x + 16, nb.y + 8, nb.width - 32, 20), toastTitle, 12, Muted, TextAnchor.MiddleLeft, FontStyle.Bold);
                Text(new Rect(nb.x + 16, nb.y + 28, nb.width - 32, 24), toastText, 14, Color.white, TextAnchor.MiddleLeft);
            }

            if (noclip && page == Page.Closed)
            {
                Rect nc = new Rect(16f, 16f, 130f, 30f);
                Round(nc, 15f, new Color(0.12f, 0.12f, 0.14f, 0.9f));
                Text(nc, "NOCLIP ON", 13, Green, TextAnchor.MiddleCenter, FontStyle.Bold);
            }

            if (page == Page.Closed) return;

            Rect phone = new Rect(w / 2f - PW / 2f, h / 2f - PH / 2f, PW, PH);

            // side buttons (power button closes the phone)
            Round(new Rect(phone.xMax - 1f, phone.y + 140f, 4f, 80f), 2f, Frame);   // volume
            Round(new Rect(phone.xMax - 1f, phone.y + 250f, 4f, 52f), 2f, Frame);   // power
            if (GUI.Button(new Rect(phone.xMax - 4f, phone.y + 245f, 12f, 62f), GUIContent.none, GUIStyle.none))
            {
                ClosePhone();
                return;
            }

            // body + metal frame + screen
            Round(phone, 38f, Frame);
            Round(new Rect(phone.x + 1.5f, phone.y + 1.5f, PW - 3f, PH - 3f), 36.5f, Body);
            Rect scr = new Rect(phone.x + 5f, phone.y + 5f, PW - 10f, PH - 10f);
            WallItem cur = walls[wallIdx];
            if (page == Page.Home) GUI.DrawTexture(scr, GetWall(cur));
            else Round(scr, 33f, AppBg);
            Color sbc = (page == Page.Home && cur.LightBg) ? (Color)new Color32(20, 20, 24, 255) : Color.white;

            // punch-hole camera
            Round(new Rect(scr.center.x - 6f, scr.y + 10f, 12f, 12f), 6f, Color.black);

            // status bar
            Text(new Rect(scr.x + 28f, scr.y + 10f, 80f, 20f), DateTime.Now.ToString("HH:mm"), 13, sbc, TextAnchor.MiddleLeft, FontStyle.Bold);
            Text(new Rect(scr.xMax - 130f, scr.y + 10f, 62f, 20f), "5G", 12, sbc, TextAnchor.MiddleRight, FontStyle.Bold);
            Round(new Rect(scr.xMax - 62f, scr.y + 14f, 24f, 12f), 3f, sbc);
            Text(new Rect(scr.xMax - 36f, scr.y + 10f, 30f, 20f), "100", 11, sbc, TextAnchor.MiddleLeft);

            switch (page)
            {
                case Page.Home: DrawHome(scr); break;
                case Page.Shop: DrawShop(scr); break;
                case Page.Sell: DrawSell(scr); break;
                case Page.Wallpaper: DrawWallpapers(scr); break;
                case Page.Bitcoin: DrawBitcoin(scr); break;
                case Page.Calc: DrawCalc(scr); break;
                case Page.Cheats: DrawCheats(scr); break;
                case Page.Vehicles: DrawVehicles(scr); break;
            }

            // gesture pill (tap = go home)
            Rect pill = new Rect(scr.center.x - 45f, scr.yMax - 14f, 90f, 4f);
            Round(pill, 2f, new Color(sbc.r, sbc.g, sbc.b, 0.8f));
            if (page != Page.Home &&
                GUI.Button(new Rect(scr.center.x - 70f, scr.yMax - 30f, 140f, 28f), GUIContent.none, GUIStyle.none))
            {
                typing = false;
                GUI.FocusControl(null);
                page = Page.Home;
            }
        }

        // ------------------------------------------------------------ HOME
        void DrawHome(Rect scr)
        {
            Color ht = walls[wallIdx].LightBg ? (Color)new Color32(20, 20, 24, 255) : Color.white;
            Text(new Rect(scr.x, scr.y + 120f, scr.width, 80f), DateTime.Now.ToString("HH:mm"), 72, ht, TextAnchor.MiddleCenter);
            Text(new Rect(scr.x, scr.y + 200f, scr.width, 26f), DateTime.Now.ToString("ddd, d MMM"), 16, ht, TextAnchor.MiddleCenter);

            float iw = 62f, x0 = scr.x + 22f;
            float gap = (scr.width - 44f - 4f * iw) / 3f;
            float y1 = scr.yMax - 300f, y2 = scr.yMax - 200f;
            DrawAppIcon(new Rect(x0, y1, iw, iw), "P", "Parts", Blue, () => { BuildShop(); page = Page.Shop; }, ht);
            DrawAppIcon(new Rect(x0 + (iw + gap), y1, iw, iw), "$", "Sell car", Green, () => { page = Page.Sell; }, ht);
            DrawAppIcon(new Rect(x0 + 2f * (iw + gap), y1, iw, iw), "B", "Bitcoin", new Color32(247, 147, 26, 255), () => { page = Page.Bitcoin; }, ht);
            DrawAppIcon(new Rect(x0 + 3f * (iw + gap), y1, iw, iw), "=", "Calculator", new Color32(20, 184, 166, 255), () => { page = Page.Calc; }, ht);
            DrawAppIcon(new Rect(x0 + (iw + gap), y2, iw, iw), "M", "Mod", new Color32(244, 63, 94, 255), () => { page = Page.Cheats; }, ht);
            DrawAppIcon(new Rect(x0 + 2f * (iw + gap), y2, iw, iw), "V", "Vehicles", new Color32(234, 179, 8, 255), () => { cars = null; BuildCars(); page = Page.Vehicles; }, ht);
            DrawAppIcon(new Rect(x0, y2, iw, iw), "W", "Wallpaper", new Color32(139, 92, 246, 255), () => { page = Page.Wallpaper; }, ht);
        }

        void DrawAppIcon(Rect r, string glyph, string label, Color c, Action onTap, Color labelColor)
        {
            Round(r, 22f, c);
            Text(r, glyph, 30, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            Text(new Rect(r.x - 15f, r.yMax + 4f, r.width + 30f, 20f), label, 13, labelColor, TextAnchor.MiddleCenter);
            if (GUI.Button(new Rect(r.x, r.y, r.width, r.height + 24f), GUIContent.none, GUIStyle.none)) onTap();
        }

        // ------------------------------------------------------------ APP BAR
        void DrawAppBar(Rect scr, string title)
        {
            Text(new Rect(scr.x + 22f, scr.y + 46f, 180f, 36f), title, 26, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            Rect pill = new Rect(scr.xMax - 142f, scr.y + 50f, 120f, 30f);
            Round(pill, 15f, Card);
            Text(pill, "$ " + tools.money.ToString("N0"), 14, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
        }

        // ------------------------------------------------------------ VEHICLES APP
        void BuildCars()
        {
            if (cars != null) return;
            cars = new List<CarItem>();

            // The game's car list. Cars added by other mods (ModUtils car generator) are in this list too.
            GameObject parent = GameObject.Find("CarsParent");
            CarList list = parent != null ? parent.GetComponent<CarList>() : null;
            if (list == null || list.Cars == null) return;

            foreach (GameObject go in list.Cars)
            {
                if (go == null) continue;
                MainCarProperties mcp = go.GetComponent<MainCarProperties>();
                string n = (mcp != null && !string.IsNullOrEmpty(mcp.CarName)) ? mcp.CarName : go.name;
                cars.Add(new CarItem { Name = n, Prefab = go });
            }
            cars = cars.OrderBy(c => c.Name).ToList();
        }

        void DrawVehicles(Rect scr)
        {
            DrawAppBar(scr, "Vehicles");

            // mode switch: junkyard / stock
            float half = (scr.width - 40f - 8f) / 2f;
            Color off = new Color32(58, 58, 64, 255);
            if (Button(new Rect(scr.x + 20f, scr.y + 92f, half, 36f), "Junkyard", carStock ? off : Blue, carStock ? (Color)new Color32(80, 80, 88, 255) : BlueHov, 13)) carStock = false;
            if (Button(new Rect(scr.x + 28f + half, scr.y + 92f, half, 36f), "Stock", carStock ? Blue : off, carStock ? BlueHov : (Color)new Color32(80, 80, 88, 255), 13)) carStock = true;

            GUI.SetNextControlName("carsearch");
            carSearch = GUI.TextField(new Rect(scr.x + 20f, scr.y + 138f, scr.width - 40f, 40f), carSearch, field);
            typing = GUI.GetNameOfFocusedControl() == "carsearch";

            string q = (carSearch ?? "").ToLowerInvariant();
            List<CarItem> list = cars.Where(c => q.Length == 0 || c.Name.ToLowerInvariant().Contains(q)).ToList();

            Rect view = new Rect(scr.x + 16f, scr.y + 190f, scr.width - 32f, scr.height - 190f - 40f);
            Rect content = new Rect(0f, 0f, view.width - 6f, Mathf.Max(view.height, list.Count * 64f));
            if (cars.Count == 0)
                Text(new Rect(view.x, view.y + 20f, view.width, 40f), "No cars found (open the app in the garage)", 13, Muted, TextAnchor.MiddleCenter);

            carScroll = GUI.BeginScrollView(view, carScroll, content, false, false, GUIStyle.none, GUIStyle.none);
            for (int i = 0; i < list.Count; i++)
            {
                Rect row = new Rect(0f, i * 64f, content.width, 56f);
                Round(row, 18f, Card);
                Text(new Rect(row.x + 16f, row.y, row.width - 110f, row.height), list[i].Name, 14, Color.white, TextAnchor.MiddleLeft);
                if (Button(new Rect(row.xMax - 90f, row.y + 11f, 80f, 34f), "Spawn", Green, new Color32(74, 222, 128, 255), 13))
                    SpawnCar(list[i]);
            }
            GUI.EndScrollView();
        }

        void SpawnCar(CarItem item)
        {
            if (item.Prefab == null || Camera.main == null) return;
            try
            {
                Transform cam = Camera.main.transform;

                // 7 m in front of you, dropped on the ground
                Vector3 pos = cam.position + cam.forward * 7f;
                RaycastHit hit;
                // start the ray just above eye level so a garage ceiling is not taken as the ground
                if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out hit, 40f)) pos.y = hit.point.y + 1.2f;
                Quaternion rot = Quaternion.Euler(0f, cam.eulerAngles.y, 0f);

                GameObject car = Instantiate(item.Prefab, pos, rot);
                car.name = item.Prefab.name;

                if (!carStock)
                {
                    // junkyard-style setup, done a few frames later like the ModUtils emulator.
                    // (SPL's EmulatorComponent is not used: it destroys its own GameObject at the end,
                    // i.e. the car itself, and it needs SimplePartLoader.)
                    StartCoroutine(JunkCarSetup(car));
                }
                else
                {
                    // same call ModUtils uses in its report code to build a stock car
                    MainCarProperties mcp = car.GetComponent<MainCarProperties>();
                    if (mcp != null)
                    {
                        mcp.SpawnPoint = pos;
                        mcp.CreatingStock(0);
                    }
                }
                Toast("Vehicles", "Spawned: " + item.Name, 2.5f);
            }
            catch (Exception ex)
            {
                Debug.LogError("[PhoneMod] Spawn car failed: " + ex);
                Toast("Vehicles", "Spawn failed (see log)", 3f);
            }
        }

        System.Collections.IEnumerator JunkCarSetup(GameObject car)
        {
            yield return null;
            yield return null;
            yield return null;
            if (car == null) yield break;
            try { SetupJunkCar(car); }
            catch (Exception ex) { Debug.LogError("[PhoneMod] Junk car setup failed: " + ex); }
        }

        // Simplified copy of SimplePartLoader's junkyard emulator (without the PaintIn3D part)
        static void SetupJunkCar(GameObject car)
        {
            MainCarProperties mcp = car.GetComponent<MainCarProperties>();
            if (mcp == null) return;

            mcp.Mileage = UnityEngine.Random.Range(1, 900000);
            mcp.Color = UnityEngine.Random.ColorHSV();
            mcp.StartOptions = UnityEngine.Random.Range(1000000000, 1999999999);
            mcp.SetCarOptions();

            GameObject plates = GameObject.Find("NumberPlates");
            if (plates != null)
            {
                mcp.NumberParent = plates.GetComponent<NumberPlateManager>();
                if (mcp.NumberParent != null) mcp.NumberParent.CreateRandomNumber();
            }
            mcp.OriginalColor = mcp.OriginalInterior >= 2 ? mcp.Color : UnityEngine.Random.ColorHSV();

            foreach (CarProperties cp in car.GetComponentsInChildren<CarProperties>())
            {
                if (cp.Cluster && !mcp.Bike)
                {
                    cp.ClusterMileage = mcp.Mileage;
                    SetUiText(cp, "MileageText", cp.ClusterMileage.ToString("F0"));
                }

                cp.Condition = UnityEngine.Random.Range(0.01f, 1f);
                if (cp.EngineOil != null) cp.EngineOil.Condition = UnityEngine.Random.Range(0.01f, 0.5f);

                Renderer r = cp.GetComponent<Renderer>();
                if (cp.Washable && r != null && mcp.DirtyMaterial != null)
                {
                    Material[] m = r.materials;
                    if (m.Length > 1) { m[1] = mcp.DirtyMaterial; r.materials = m; }
                }

                if (cp.Tire)
                {
                    if (cp.Condition < 0.1f) cp.PartIsOld = true;
                    cp.TirePressure = Mathf.Min(UnityEngine.Random.Range(0.1f, 4f), 2f);
                }

                if (cp.Interior) cp.OriginalInterior = mcp.OriginalInterior;

                if (cp.NumberPlate && r != null && mcp.NumberParent != null)
                {
                    Material[] m = r.materials;
                    if (m.Length > 7)
                    {
                        NumberPlateManager n = mcp.NumberParent;
                        m[2] = n.M1; m[3] = n.M2; m[4] = n.M3; m[5] = n.M4; m[6] = n.M5; m[7] = n.M6;
                        r.materials = m;
                        cp.One = n.M1; cp.Two = n.M2; cp.Three = n.M3; cp.Four = n.M4; cp.Five = n.M5; cp.Six = n.M6;
                    }
                }
            }

            // remove some parts, like the junkyard does (JunkSpawnChance)
            foreach (CarProperties cp in car.GetComponentsInChildren<CarProperties>())
            {
                if (!cp.SinglePart) continue;
                bool remove = cp.JunkSpawnChance == 1 ||
                              (cp.JunkSpawnChance == 2 && cp.Condition >= 0.6f) ||
                              (cp.JunkSpawnChance == 3 && cp.Condition >= 0.8f);
                if (remove)
                {
                    Pickup pu = cp.GetComponent<Pickup>();
                    if (pu != null && !cp.Tire) pu.BRAKE2();
                    PickupDoor pd = cp.GetComponent<PickupDoor>();
                    if (pd != null) pd.BRAKE2();
                    Destroy(cp.gameObject);
                    continue;
                }
                if (cp.JunkSpawnChance == 4 && cp.Condition <= 0.4f && cp.OldMaterial != null)
                {
                    cp.Condition = 0.15f;
                    cp.PartIsOld = true;
                    Renderer r = cp.GetComponent<Renderer>();
                    if (r != null) r.sharedMaterial = cp.OldMaterial;
                }
                if (cp.Tintable && !cp.RuinedMaterial) cp.Condition = 1f;
                if (cp.CantRust && !cp.MeshLittleDamaged) cp.Condition = 1f;
            }

            mcp.ResetWheelControllers();

            // rough start price: car price * average part condition
            float sum = 0f; int count = 0;
            foreach (CarProperties cp in car.GetComponentsInChildren<CarProperties>())
                if (cp.SinglePart) { sum += cp.Condition; count++; }
            mcp.CarPriceStart = count > 0 ? mcp.CarPrice * (sum / count) : 250f;
        }

        // CarProperties.MileageText is a UnityEngine.UI.Text: set by reflection (UnityEngine.UI not referenced)
        static void SetUiText(object owner, string fieldName, string value)
        {
            FieldInfo f = owner.GetType().GetField(fieldName);
            object ui = f != null ? f.GetValue(owner) : null;
            if (ui == null || (ui is UnityEngine.Object && !(UnityEngine.Object)ui)) return;
            PropertyInfo t = ui.GetType().GetProperty("text");
            if (t != null) t.SetValue(ui, value, null);
        }

        // ------------------------------------------------------------ MOD APP (noclip / money / save)
        void DrawCheats(Rect scr)
        {
            DrawAppBar(scr, "Mod");

            // noclip card with a switch
            Rect c1 = new Rect(scr.x + 20f, scr.y + 96f, scr.width - 40f, 92f);
            Round(c1, 24f, Card);
            Text(new Rect(c1.x + 18f, c1.y + 16f, 170f, 26f), "Noclip", 18, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            lbl.wordWrap = true;
            Text(new Rect(c1.x + 18f, c1.y + 44f, 170f, 36f), "WASD, Space up, Ctrl down, Shift fast", 11, Muted, TextAnchor.UpperLeft);
            lbl.wordWrap = false;
            Rect sw = new Rect(c1.xMax - 18f - 56f, c1.y + 31f, 56f, 30f);
            Round(sw, 15f, noclip ? Green : GreyBtn);
            Round(new Rect(noclip ? sw.xMax - 27f : sw.x + 3f, sw.y + 3f, 24f, 24f), 12f, Color.white);
            if (GUI.Button(new Rect(c1.x, c1.y, c1.width, c1.height), GUIContent.none, GUIStyle.none)) SetNoclip(!noclip);

            // give money card
            Rect c2 = new Rect(c1.x, c1.yMax + 12f, c1.width, 150f);
            Round(c2, 24f, Card);
            Text(new Rect(c2.x + 18f, c2.y + 10f, 200f, 24f), "Give money", 18, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            float bw = (c2.width - 36f - 8f) / 2f;
            float[] amounts = { 1000f, 10000f, 100000f, 1000000f };
            for (int i = 0; i < 4; i++)
            {
                Rect b = new Rect(c2.x + 18f + (i % 2) * (bw + 8f), c2.y + 42f + (i / 2) * 52f, bw, 44f);
                if (Button(b, "+$" + amounts[i].ToString("N0"), Blue, BlueHov, 14)) GiveMoney(amounts[i]);
            }

            // quick save card
            Rect c3 = new Rect(c1.x, c2.yMax + 12f, c1.width, 130f);
            Round(c3, 24f, Card);
            Text(new Rect(c3.x + 18f, c3.y + 10f, 200f, 24f), "Quick save", 18, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            if (Button(new Rect(c3.x + 18f, c3.y + 42f, c3.width - 36f, 46f), "Save now", Green, new Color32(74, 222, 128, 255), 15)) QuickSave();
            Text(new Rect(c3.x + 18f, c3.y + 94f, c3.width - 36f, 22f), lastSave == "" ? "Not saved yet" : "Last save: " + lastSave, 12, Muted, TextAnchor.MiddleLeft);
        }

        void GiveMoney(float amount)
        {
            tools.money += amount;
            PlayCashSound();
            Toast("Mod", "+$" + amount.ToString("N0"), 2f);
        }

        void SetNoclip(bool on)
        {
            if (!FindPlayer()) { Toast("Mod", "Player not found", 2.5f); return; }
            GameObject pl = player;

            if (on)
            {
                if (PlayerInCar()) { Toast("Mod", "Get out of the car first", 2.5f); return; }
                ncPlayer = pl.transform;
                ncRb = pl.GetComponentInChildren<Rigidbody>();
                ncCols.Clear();
                foreach (Collider c in pl.GetComponentsInChildren<Collider>())
                {
                    if (c.enabled && !c.isTrigger) { c.enabled = false; ncCols.Add(c); }
                }
                if (ncRb != null)
                {
                    ncGravity = ncRb.useGravity;
                    ncKinematic = ncRb.isKinematic;
                    ncRb.useGravity = false;
                    ncRb.isKinematic = true;
                }
                noclip = true;
                Toast("Mod", "Noclip ON", 2f);
            }
            else
            {
                foreach (Collider c in ncCols) { if (c != null) c.enabled = true; }
                ncCols.Clear();
                if (ncRb != null)
                {
                    ncRb.isKinematic = ncKinematic;
                    ncRb.useGravity = ncGravity;
                    if (!ncRb.isKinematic) ncRb.velocity = Vector3.zero;
                }
                noclip = false;
                Toast("Mod", "Noclip OFF", 2f);
            }
        }

        void QuickSave()
        {
            try
            {
                tools t = FindPlayer() ? player.GetComponent<tools>() : null;
                object saver = t != null ? t.saver : null;
                if (saver == null) { Toast("Mod", "Saver not found", 3f); return; }

                // call Saver.Save() by reflection (only if it needs no required arguments)
                MethodInfo m = saver.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(x => x.Name == "Save" && x.GetParameters().All(pi => pi.IsOptional))
                    .OrderBy(x => x.GetParameters().Length)
                    .FirstOrDefault();
                if (m == null) { Toast("Mod", "Saver.Save() needs arguments, check it in dnSpy", 4f); return; }

                object[] args = m.GetParameters().Select(pi => pi.DefaultValue).ToArray();
                m.Invoke(saver, args);
                lastSave = DateTime.Now.ToString("HH:mm:ss");
                Toast("Mod", "Game saved", 2.5f);
            }
            catch (Exception ex)
            {
                Debug.LogError("[PhoneMod] Quick save failed: " + ex);
                Toast("Mod", "Save failed (see log)", 3f);
            }
        }

        // ------------------------------------------------------------ BITCOIN APP
        static readonly Color Red = new Color32(239, 68, 68, 255);

        void DrawBitcoin(Rect scr)
        {
            DrawAppBar(scr, "Bitcoin");

            float pct = btcPrev > 0f ? (btcPrice - btcPrev) / btcPrev * 100f : 0f;
            bool up = pct >= 0f;

            // price card
            Rect c1 = new Rect(scr.x + 20f, scr.y + 96f, scr.width - 40f, 190f);
            Round(c1, 24f, Card);
            Text(new Rect(c1.x + 16f, c1.y + 12f, 120f, 20f), "1 BTC", 13, Muted, TextAnchor.MiddleLeft);
            Text(new Rect(c1.x + 100f, c1.y + 12f, c1.width - 116f, 20f), "Next price in " + Mathf.CeilToInt(Mathf.Max(0f, nextBtcAt - Time.time)) + " s", 13, Muted, TextAnchor.MiddleRight);
            Text(new Rect(c1.x + 16f, c1.y + 32f, c1.width - 32f, 50f), "$ " + btcPrice.ToString("N0"), 36, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            Text(new Rect(c1.x + 16f, c1.y + 82f, 200f, 22f), (up ? "+" : "") + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%", 14, up ? Green : Red, TextAnchor.MiddleLeft, FontStyle.Bold);

            // mini chart: last prices, newest on the right
            float inner = c1.width - 32f, bw = (inner - 11f * 4f) / 12f, bottom = c1.yMax - 14f;
            for (int i = 0; i < btcHist.Count; i++)
            {
                float hh = Mathf.Lerp(8f, 52f, Mathf.InverseLerp(BtcMin, BtcMax, btcHist[i]));
                Color col = i == 0 ? GreyBtn : (btcHist[i] >= btcHist[i - 1] ? Green : Red);
                float bx = c1.x + 16f + (12 - btcHist.Count + i) * (bw + 4f);
                Round(new Rect(bx, bottom - hh, bw, hh), 4f, col);
            }

            // wallet card
            Rect c2 = new Rect(c1.x, c1.yMax + 12f, c1.width, 78f);
            Round(c2, 24f, Card);
            Text(new Rect(c2.x + 16f, c2.y + 10f, 140f, 20f), "You own", 13, Muted, TextAnchor.MiddleLeft);
            Text(new Rect(c2.x + 16f, c2.y + 30f, 180f, 36f), btcHeld.ToString("0.0000", CultureInfo.InvariantCulture) + " BTC", 22, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
            Text(new Rect(c2.xMax - 120f, c2.y + 10f, 104f, 20f), "Value", 13, Muted, TextAnchor.MiddleRight);
            Text(new Rect(c2.xMax - 130f, c2.y + 30f, 114f, 36f), "$ " + (btcHeld * btcPrice).ToString("N0"), 16, Color.white, TextAnchor.MiddleRight, FontStyle.Bold);

            float bw3 = (c1.width - 16f) / 3f;
            Text(new Rect(c1.x + 4f, c2.yMax + 10f, 100f, 20f), "Buy", 13, Muted, TextAnchor.MiddleLeft);
            float by = c2.yMax + 32f;
            if (Button(new Rect(c1.x, by, bw3, 44f), "$1,000", Blue, BlueHov, 14)) BtcBuy(1000f);
            if (Button(new Rect(c1.x + bw3 + 8f, by, bw3, 44f), "$5,000", Blue, BlueHov, 14)) BtcBuy(5000f);
            if (Button(new Rect(c1.x + 2f * (bw3 + 8f), by, bw3, 44f), "Max", Blue, BlueHov, 14)) BtcBuy(Mathf.Floor(tools.money));

            Text(new Rect(c1.x + 4f, by + 54f, 100f, 20f), "Sell", 13, Muted, TextAnchor.MiddleLeft);
            float sy = by + 76f;
            Color gh = new Color32(80, 80, 88, 255);
            if (Button(new Rect(c1.x, sy, bw3, 44f), "25%", GreyBtn, gh, 14)) BtcSell(0.25f);
            if (Button(new Rect(c1.x + bw3 + 8f, sy, bw3, 44f), "50%", GreyBtn, gh, 14)) BtcSell(0.5f);
            if (Button(new Rect(c1.x + 2f * (bw3 + 8f), sy, bw3, 44f), "All", Green, new Color32(74, 222, 128, 255), 14)) BtcSell(1f);
        }

        void BtcBuy(float dollars)
        {
            if (dollars < 1f || tools.money < dollars) { Toast("Bitcoin", "Not enough money", 2.5f); return; }
            float qty = Mathf.Round(dollars / btcPrice * 10000f) / 10000f;
            if (qty <= 0f) return;
            tools.money -= dollars;
            btcHeld = Mathf.Round((btcHeld + qty) * 10000f) / 10000f;
            SaveBtc();
            PlayCashSound();
            Toast("Bitcoin", "Bought " + qty.ToString("0.0000", CultureInfo.InvariantCulture) + " BTC", 2.5f);
        }

        void BtcSell(float fraction)
        {
            if (btcHeld <= 0f) { Toast("Bitcoin", "You have no bitcoin", 2.5f); return; }
            float qty = fraction >= 1f ? btcHeld : Mathf.Round(btcHeld * fraction * 10000f) / 10000f;
            if (qty <= 0f) return;
            float gain = Mathf.Round(qty * btcPrice);
            tools.money += gain;
            btcHeld = fraction >= 1f ? 0f : Mathf.Round((btcHeld - qty) * 10000f) / 10000f;
            SaveBtc();
            PlayCashSound();
            Toast("Bitcoin", "Sold " + qty.ToString("0.0000", CultureInfo.InvariantCulture) + " BTC for $" + gain.ToString("N0"), 3f);
        }

        void SaveBtc()
        {
            PlayerPrefs.SetFloat("PhoneMod_BTC", btcHeld);
            PlayerPrefs.Save();
        }

        // ------------------------------------------------------------ CALCULATOR APP
        static readonly string[] CalcKeys =
        {
            "C", "+/-", "%", "/",
            "7", "8", "9", "x",
            "4", "5", "6", "-",
            "1", "2", "3", "+",
            "0", ".", "<", "="
        };

        void DrawCalc(Rect scr)
        {
            Text(new Rect(scr.x + 24f, scr.y + 110f, scr.width - 48f, 24f), calcExpr, 15, Muted, TextAnchor.MiddleRight);
            int size = calcDisp.Length <= 7 ? 64 : (calcDisp.Length <= 10 ? 46 : 34);
            Text(new Rect(scr.x + 20f, scr.y + 140f, scr.width - 40f, 90f), calcDisp, size, Color.white, TextAnchor.MiddleRight);

            float gap = 8f, bw = (scr.width - 32f - 3f * gap) / 4f, bh = 62f;
            float y0 = scr.yMax - 50f - (5f * bh + 4f * gap);
            Color num = Field, fn = GreyBtn, op = Blue;
            for (int i = 0; i < CalcKeys.Length; i++)
            {
                string k = CalcKeys[i];
                Rect rc = new Rect(scr.x + 16f + (i % 4) * (bw + gap), y0 + (i / 4) * (bh + gap), bw, bh);
                bool isOp = k == "/" || k == "x" || k == "-" || k == "+";
                bool isFn = k == "C" || k == "+/-" || k == "%" || k == "<";
                Color n = k == "=" ? Green : (isOp ? op : (isFn ? fn : num));
                Color h = k == "=" ? (Color)new Color32(74, 222, 128, 255) : (isOp ? BlueHov : (Color)new Color32(80, 80, 88, 255));
                if (Button(rc, k, n, h, 22)) CalcPress(k);
            }
        }

        static string CalcFmt(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "Error";
            if (Math.Abs(v) >= 1e12) return v.ToString("E4", CultureInfo.InvariantCulture);
            return Math.Round(v, 10).ToString("0.##########", CultureInfo.InvariantCulture);
        }

        double CalcVal()
        {
            double v;
            return double.TryParse(calcDisp, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
        }

        void CalcReset() { calcDisp = "0"; calcExpr = ""; calcAcc = 0; calcOp = null; calcFresh = true; }

        void CalcCompute()
        {
            double b = CalcVal(), r = calcAcc;
            switch (calcOp)
            {
                case "+": r = calcAcc + b; break;
                case "-": r = calcAcc - b; break;
                case "x": r = calcAcc * b; break;
                case "/":
                    if (b == 0.0) { CalcReset(); calcDisp = "Error"; return; }
                    r = calcAcc / b;
                    break;
            }
            calcAcc = r;
            calcDisp = CalcFmt(r);
        }

        void CalcPress(string k)
        {
            if (calcDisp == "Error" && k != "C") CalcReset();

            if (k.Length == 1 && char.IsDigit(k[0]))
            {
                if (calcFresh || calcDisp == "0") calcDisp = k;
                else if (calcDisp.Length < 12) calcDisp += k;
                calcFresh = false;
            }
            else if (k == ".")
            {
                if (calcFresh) calcDisp = "0.";
                else if (!calcDisp.Contains(".")) calcDisp += ".";
                calcFresh = false;
            }
            else if (k == "C") CalcReset();
            else if (k == "<")
            {
                if (!calcFresh) calcDisp = calcDisp.Length > 1 ? calcDisp.Substring(0, calcDisp.Length - 1) : "0";
                if (calcDisp == "-") calcDisp = "0";
            }
            else if (k == "+/-")
            {
                if (calcDisp != "0") calcDisp = calcDisp.StartsWith("-") ? calcDisp.Substring(1) : "-" + calcDisp;
            }
            else if (k == "%")
            {
                calcDisp = CalcFmt(CalcVal() / 100.0);
                calcFresh = true;
            }
            else if (k == "=")
            {
                if (calcOp != null)
                {
                    CalcCompute();
                    calcOp = null;
                    calcExpr = "";
                    calcFresh = true;
                }
            }
            else // + - x /
            {
                if (calcOp != null && !calcFresh)
                {
                    CalcCompute();
                    if (calcDisp == "Error") return;
                }
                else calcAcc = CalcVal();
                calcOp = k;
                calcFresh = true;
                calcExpr = CalcFmt(calcAcc) + " " + k;
            }
        }

        // ------------------------------------------------------------ WALLPAPER APP
        void DrawWallpapers(Rect scr)
        {
            Text(new Rect(scr.x + 22f, scr.y + 46f, 220f, 36f), "Wallpaper", 26, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);

            const float tw = 130f, th = 286f, gap = 10f, rowH = th + 30f;
            int rows = (walls.Count + 1) / 2;
            Rect view = new Rect(scr.x + 20f, scr.y + 96f, scr.width - 40f, scr.height - 96f - 40f);
            Rect content = new Rect(0f, 0f, view.width - 6f, rows * rowH);

            wallScroll = GUI.BeginScrollView(view, wallScroll, content, false, false, GUIStyle.none, GUIStyle.none);
            for (int i = 0; i < walls.Count; i++)
            {
                Rect tile = new Rect((i % 2) * (tw + gap) + 4f, (i / 2) * rowH + 4f, tw, th);
                if (i == wallIdx) Round(new Rect(tile.x - 4f, tile.y - 4f, tw + 8f, th + 8f), 27f, Blue);
                GUI.DrawTexture(tile, GetWall(walls[i]));
                Text(new Rect(tile.x, tile.yMax + 4f, tw, 20f), walls[i].Name, 13, Color.white, TextAnchor.MiddleCenter);
                if (GUI.Button(tile, GUIContent.none, GUIStyle.none))
                {
                    wallIdx = i;
                    PlayerPrefs.SetInt("PhoneMod_WP", i);
                    PlayerPrefs.Save();
                }
            }
            GUI.EndScrollView();
        }

        // ------------------------------------------------------------ PARTS APP
        void BuildShop()
        {
            if (shop != null && shop.Count > 0) return;
            shop = new List<ShopItem>();
            foreach (GameObject part in GetGameParts())
            {
                if (part == null) continue;
                Partinfo info = part.GetComponent<Partinfo>();
                if (info == null || info.price <= 0f) continue;
                string n = string.IsNullOrEmpty(info.RenamedPrefab) ? part.name : info.RenamedPrefab;
                shop.Add(new ShopItem { Name = n, Price = info.price, Info = info });
            }
            shop = shop.OrderBy(i => i.Name).ToList();
        }

        void DrawShop(Rect scr)
        {
            DrawAppBar(scr, "Parts");

            GUI.SetNextControlName("search");
            search = GUI.TextField(new Rect(scr.x + 20f, scr.y + 96f, scr.width - 40f, 40f), search, field);
            typing = GUI.GetNameOfFocusedControl() == "search";

            if (shop.Count == 0)
            {
                Text(new Rect(scr.x, scr.y + 170f, scr.width, 24f), "No parts available", 15, Muted, TextAnchor.MiddleCenter);
                return;
            }

            string q = (search ?? "").ToLowerInvariant();
            List<ShopItem> list = shop.Where(i => q.Length == 0 || i.Name.ToLowerInvariant().Contains(q)).Take(80).ToList();

            Rect view = new Rect(scr.x + 16f, scr.y + 148f, scr.width - 32f, scr.height - 148f - 40f);
            Rect content = new Rect(0f, 0f, view.width - 6f, Mathf.Max(view.height, list.Count * 64f));

            scroll = GUI.BeginScrollView(view, scroll, content, false, false, GUIStyle.none, GUIStyle.none);
            for (int i = 0; i < list.Count; i++)
            {
                ShopItem item = list[i];
                Rect row = new Rect(0f, i * 64f, content.width, 56f);
                Round(row, 18f, Card);
                Text(new Rect(row.x + 16f, row.y, row.width - 110f, row.height), item.Name, 14, Color.white, TextAnchor.MiddleLeft);
                if (Button(new Rect(row.xMax - 90f, row.y + 11f, 80f, 34f), "$" + item.Price.ToString("N0"), Blue, BlueHov, 13))
                    Buy(item);
            }
            GUI.EndScrollView();
        }

        void Buy(ShopItem item)
        {
            if (tools.money < item.Price) { Toast("Parts", "Not enough money", 2.5f); return; }

            if (Camera.main == null) return;
            Transform cam = Camera.main.transform;
            if (spawnSpot == null) spawnSpot = new GameObject("PHONEMOD_SPAWNSPOT");
            spawnSpot.transform.position = cam.position + cam.forward * 1.5f;
            spawnSpot.transform.rotation = Quaternion.identity;

            tools.money -= item.Price;
            PlayCashSound();

            tools.SpawnSpot = spawnSpot; // the game spawns the part here
            item.Info.SpawnThis();
            Toast("Parts", "Bought: " + item.Name, 2.5f);
        }

        // ------------------------------------------------------------ SELL APP
        void DrawSell(Rect scr)
        {
            DrawAppBar(scr, "Sell car");
            Rect card = new Rect(scr.x + 20f, scr.y + 110f, scr.width - 40f, 300f);
            Round(card, 28f, Card);

            switch (sellState)
            {
                case SellState.Idle:
                    Text(new Rect(card.x, card.y + 50f, card.width, 30f), "Stand next to your car", 16, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
                    Text(new Rect(card.x, card.y + 82f, card.width, 24f), "A buyer answers in " + OfferDelay + " s", 13, Muted, TextAnchor.MiddleCenter);
                    if (Button(new Rect(card.x + 24f, card.yMax - 80f, card.width - 48f, 50f), "Find buyers", Blue, BlueHov, 16))
                    {
                        if (PlayerInCar()) { Toast("Sell car", "Get out of the car first", 2.5f); break; }
                        targetCar = FindNearestCar();
                        if (targetCar == null) { Toast("Sell car", "No car nearby", 2.5f); break; }
                        offerAt = Time.time + OfferDelay;
                        sellState = SellState.Waiting;
                    }
                    break;

                case SellState.Waiting:
                    float left = Mathf.Max(0f, offerAt - Time.time);
                    Text(new Rect(card.x, card.y + 36f, card.width, 26f), "Looking for buyers...", 15, Muted, TextAnchor.MiddleCenter);
                    Text(new Rect(card.x, card.y + 66f, card.width, 70f), Mathf.CeilToInt(left) + " s", 52, Color.white, TextAnchor.MiddleCenter);
                    Rect bar = new Rect(card.x + 30f, card.y + 160f, card.width - 60f, 8f);
                    Round(bar, 4f, Field);
                    float p = 1f - left / OfferDelay;
                    if (p > 0.02f) Round(new Rect(bar.x, bar.y, bar.width * p, bar.height), 4f, Blue);
                    if (Button(new Rect(card.x + 24f, card.yMax - 80f, card.width - 48f, 50f), "Cancel", GreyBtn, new Color32(80, 80, 88, 255), 15))
                    {
                        sellState = SellState.Idle;
                        targetCar = null;
                    }
                    break;

                case SellState.Offer:
                    Text(new Rect(card.x, card.y + 34f, card.width, 24f), "A buyer offers", 14, Muted, TextAnchor.MiddleCenter);
                    Text(new Rect(card.x, card.y + 62f, card.width, 70f), "$ " + offerValue.ToString("N0"), 44, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
                    float bw = (card.width - 48f - 10f) / 2f;
                    if (Button(new Rect(card.x + 24f, card.yMax - 80f, bw, 50f), "Accept", Green, new Color32(74, 222, 128, 255), 15))
                        AcceptOffer();
                    if (Button(new Rect(card.x + 24f + bw + 10f, card.yMax - 80f, bw, 50f), "Decline", GreyBtn, new Color32(80, 80, 88, 255), 15))
                    {
                        offerAt = Time.time + OfferDelay; // next offer after another 15 s
                        sellState = SellState.Waiting;
                    }
                    break;
            }
        }

        GameObject FindNearestCar()
        {
            if (Camera.main == null) return null;
            Vector3 p = Camera.main.transform.position;
            GameObject best = null;
            float bestD = MaxCarDistance;
            foreach (GameObject car in GameObject.FindGameObjectsWithTag("Vehicle"))
            {
                float d = Vector3.Distance(p, car.transform.position);
                if (d < bestD) { bestD = d; best = car; }
            }
            return best;
        }

        float ComputeOffer(GameObject car)
        {
            float total = 0f;
            foreach (Partinfo pi in car.GetComponentsInChildren<Partinfo>())
                total += pi.price;

            float value = Mathf.Max(MinOffer, total * SellRatio);
            return Mathf.Round(value * UnityEngine.Random.Range(0.8f, 1.2f));
        }

        void AcceptOffer()
        {
            if (targetCar == null) { Toast("Sell car", "The car is gone", 2.5f); sellState = SellState.Idle; return; }
            if (PlayerInCar()) { Toast("Sell car", "Get out of the car first", 2.5f); return; }
            if (Vector3.Distance(Camera.main.transform.position, targetCar.transform.position) > MaxCarDistance + 5f)
            {
                Toast("Sell car", "The car is too far away", 2.5f);
                sellState = SellState.Idle;
                targetCar = null;
                return;
            }

            tools.money += offerValue;
            PlayCashSound();
            Destroy(targetCar);
            targetCar = null;
            sellState = SellState.Idle;
            Toast("Sell car", "Car sold for $" + offerValue.ToString("N0"), 3f);
        }

        void Toast(string title, string msg, float seconds)
        {
            toastTitle = title;
            toastText = msg;
            toastUntil = Time.time + seconds;
        }
    }
}

namespace PhoneMod
{
    // Wallpaper images stored as base64 so they are inside the DLL
    static class EmbeddedWalls
    {
        public const string TopMecanic = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAG/Ab8DASIAAhEBAxEB/8QAHQABAAICAwEBAAAAAAAAAAAAAAYHBQgCAwQJAf/EAF8QAAEDAgMEBgUFBg8NBwUBAAIAAwQFEgYTIgEHMkIUI1JicoIIEZKishUzQ8LSFiQxU2NzFxghJSc0N0FEYXWDs+LwNTZFUVRlcXSTlKPD8iYoVWSBsdMJOJHB0eH/xAAaAQEBAQEBAQEAAAAAAAAAAAAAAgMEBQEG/8QALREBAQACAQQABQQCAQUAAAAAAAIDEiIBBBMyBRExQlIUITNRI0NxFSRhYqH/2gAMAwEAAhEDEQA/ANMkREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQEREBERAREQERctgkSDiuVq9sSlVKTAk1CNDedixvn3RH1i3/pWTj4Wqj2FXcRt5PQmjtLZd1n+Iit7KCOrlaXZUrfwo43gRjFe2cJC4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JGzVmF7K63JBZmlBvVhD0kN2taiAVVnu0KWXG1KYcIdnnESFe/EHpB7qaVEJ1rEnyo/bcMeDGccIvMQiI+YloJskuDw2+yuXSnu0PsoLZ33b5K1vHmNxtjblLoUc72IY7biIvxju3mLuq3d3XpG4Hw/gakUWdTKzslRIwsu7GWQJu7ulctSelvdoUGS53fZQbbbyfSPwbXMAVyjUqBXmpk+E5HaJ1gBbEi7WpUbuFxw3gDHTFYqUd6VT8om3wa+cES5hVfuSGyZLtL0i8xaNz48PZQbkl6Ue78S/uXiL/dm//kXUPpQ7vxedcGk4gLM/IN8XtLTSVJ6wchwSHwrp6Y/bbcPq8KDcauek5gWoUWdBapdczJMZxkbmW7dQ29pan0mZMplRjTIMjJdjE24JW+sbh+JYkZLl2q0l3xZA5jhOuZd3DpQbW4P9KOkw8OxItcw3UnKg2NrrkVxvLLw3FcsHUPSIpsrefGrAUOY3RMhpt4St6QLgkXDqtIdQrXCVKbFschwSLwrzdKeu4hQbpF6VGAxH+4+IPKy39pUl6R28ij7ycR0aoUGPOjjAiE250kREricuG20lTzk6Q5w2iPdFcOlyvxvu7EGwe570ga7hQWKXiZp6t0odLZ7P2ywPwuD7yvyk7/d0tRbzPusbhkI6mpUZ1ovhtWgPTZX773uivMRkRXbdv6qDeDHnpO4CpFPMcNOPYgqGoW7WDajiXeJwRIh8IrUDHeLK3jXET9er0vpM18v3tItjygA8oio9cXaQiLb+FNRxREQEREBERAREQEREBERAXY2ZAVwEQl3V1ogyjNbqjVtk1z1DyruexFVXvwybVhvWnrTWX35vY5OlOEWbIIrl5C23bfWuKI+CIiAiIgIiICIiDkuKIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiIC5LiiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIgIiICIiAiIg/9k=";

        public const string MyGarage = "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oWnjkHXmkcZP6lZIhsNioqdscNkt0LB0DW0zf/JVRmcdW8q7HOO3Mq+ENs+FtptrXBzbbQNPqKdoX2NY1rA1rQxo7BnQLuPoit7kU0zjwx/E7/qK5DQPM/quUVSowPRcYHoFyiAIiIAmAiIAiIgCIiAIiIAiIgCIiAIiIAiIgCIiAIiIAiIgCIiAIiIAmAiIDjAPcBMD0XKIBgJgIiAYHoiIgCYHoiIBgeiYCIgGAmAiIAiIgCIiaAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQBERAEREAREQH//2Q==";
    }
}
