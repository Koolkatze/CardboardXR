using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CardboardXR.Demos
{
    // The app library, like visionOS's or Quest's: a grid of app icons and names above the settings
    // panel, which its Apps button shows and hides. The lobby comes first, then each demo in the
    // catalog; pick one and the panel closes and that app opens, from wherever you are, so there's no
    // need to go back to the lobby between demos (DemoManager restores what one demo switched on
    // before applying the next's needs). The app running now is marked with a dot under its name.
    // Looking at an app shows its one-line tagline under the grid. Entering the lobby opens the
    // settings panel with the library showing (Open, from LobbyFlow), since the lobby is only a room
    // to start from. Part of the settings panel, so it follows the panel and
    // closes with it. Tiles are built at runtime from the catalog, so a new demo needs only a catalog
    // entry. A page holds k_Rows rows, so the library stays as short however many demos there are; with
    // more apps than that, arrows either side of the grid turn the page, dots under it say which page
    // it is (or "3 of 20", past k_MaxPageDots pages), and it opens on the page with the app that's
    // running. The "libraryRows" launch option makes pages shorter, to try paging with few demos.
    //   On Android a rail at the library's left, like visionOS's tab bar, switches between two sections:
    // XR, the lobby and demos, and Android, the phone's own apps (AndroidApps), with their icons, in
    // the launcher's order. Picking one opens it on a screen in the scene (AppScreen, with Shizuku) or
    // else flat, in landscape if it can turn; the rail's Landscape switch, shown in that section, turns
    // that off and on, and the tagline's prompt says which way apps will open. The Android section is laid out as paged
    // from the start (AndroidLayoutCount), so it keeps its size when the list comes in, and the icons
    // of the page showing are filled in as they load. The library reopens on the section it was left
    // on, except from the lobby (Open), which shows XR. Lives on an object that's always active (the
    // Demos object), and shows and hides m_Root. Logs [Demos].
    public class AppLibrary : MonoBehaviour
    {
        public enum Section
        {
            Xr,
            Android,
        }

        // In dp: each app's cell, its icon tile, and the gaps between cells and around the grid.
        public const float k_CellWidth = 128f;
        // Kept short, so it sits at eye level with the settings panel close below it.
        public const float k_CellHeight = 124f;
        const float k_IconSize = 72f;
        const float k_IconRadius = 18f;
        const float k_Gap = 8f;
        const float k_Padding = 16f;
        const float k_TaglineHeight = 28f;
        public const int k_Columns = 3;
        // Rows a page: two keep the library short enough to sit at eye level with the settings panel
        // close below it. A third row would push the panel 5 degrees further down, and each row after
        // that 5 more.
        public const int k_Rows = 2;
        // When there's more than a page: an arrow either side of the grid, and a row of page dots
        // between the grid and the tagline.
        const float k_ArrowSize = M3Theme.k_Target;
        const float k_DotsHeight = 14f;
        const float k_DotSize = 6f;
        const float k_DotSpacing = 12f;
        // Past this many pages (a phone's apps make twenty or more), the dots' row says "3 of 20" instead.
        public const int k_MaxPageDots = 8;
        // The rail, in dp: navigation-rail items like the toolbar's, stacked, with a divider before the
        // Landscape switch, and its gap from the library.
        public const float k_RailItemWidth = 88f;
        public const float k_RailItemHeight = 76f;
        const float k_RailPadding = 8f;
        const float k_RailDividerSpace = 17f;
        public const float k_RailGap = 16f;
        const string k_Prompt = "Look at an app to see what it is";
        // The Android section's, which says whether its apps open in XR (on a screen in the scene, with
        // Shizuku) or flat.
        const string k_XrPrompt = "Look at an app to see what it is; it opens on a screen here";
        const string k_FlatPrompt = "Start Shizuku to open apps on a screen here, not flat";
        // Picks this soon after the library appears are ignored: it opens where the user is looking,
        // and the pinch that opened it (or a flicker of it) landed on a tile 0.6 s later, reopening the
        // demo just left.
        const float k_PickGuardSeconds = 0.8f;
        // How often the page showing looks for icons that have loaded since.
        const float k_IconCheckSeconds = 0.2f;

        [SerializeField] DemoManager m_Demos;
        [SerializeField] SettingsSummoner m_Settings;
        [Tooltip("The settings panel's Apps button, shown selected while the library shows.")]
        [SerializeField] M3Button m_AppsButton;
        [Tooltip("The library's surface, under the settings panel, shown and hidden.")]
        [SerializeField] RectTransform m_Root;
        [Tooltip("Where the tiles go.")]
        [SerializeField] RectTransform m_Grid;
        [SerializeField] TMP_Text m_Tagline;
        [Header("The lobby, which isn't in the catalog")]
        [SerializeField] string m_LobbyTitle = "Lobby";
        [SerializeField] string m_LobbyTagline = "A quiet room to start from";
        [SerializeField] string m_LobbyIcon = "home";
        [SerializeField] Color m_LobbyTint = new(0.29f, 0.36f, 0.54f);

        // One of the phone's apps in the Android section.
        sealed class AndroidTile
        {
            public AndroidApps.App App;
            public GameObject Cell;
            public RawImage Icon;
            public GameObject Placeholder;
            public AppTileGaze Gaze;
        }

        readonly List<(DemoCatalog.Entry entry, GameObject running)> m_Tiles = new();
        readonly List<GameObject> m_Cells = new();
        readonly List<AndroidTile> m_AndroidTiles = new();
        readonly List<AppTileGaze> m_RailGazes = new();
        readonly List<Image> m_Dots = new();
        // The page each section was left on.
        readonly int[] m_SectionPages = new int[2];
        RectTransform m_XrCells;
        RectTransform m_AndroidCells;
        M3Button m_Previous;
        M3Button m_Next;
        TMP_Text m_PageLabel;
        TMP_Text m_Message;
        RectTransform m_Rail;
        M3Button m_XrTab;
        M3Button m_AndroidTab;
        M3Button m_LandscapeSwitch;
        AppTileGaze m_LandscapeGaze;
        Image m_RailDivider;
        Section m_Section = Section.Xr;
        string m_AndroidPrompt = k_FlatPrompt;
        int m_AndroidVersion = -1;
        int m_Rows = k_Rows;
        int m_Page;
        int m_Pages = 1;
        float m_ShownAt = float.NegativeInfinity;
        float m_NextIconCheck;
        bool m_Built;

        public bool IsShown => m_Root != null && M3Fade.IsShown(m_Root.gameObject);
        // Which page shows, from 0, and how many there are.
        public int Page => m_Page;
        public int PageCount => m_Pages;
        public Section CurrentSection => m_Section;

        public static int PerPage(int rows) => k_Columns * Mathf.Max(1, rows);

        // How many pages this many apps take, at rows a page.
        public static int Pages(int apps, int rows = k_Rows) => Mathf.Max(1, (apps + PerPage(rows) - 1) / PerPage(rows));

        // The page an app is on, by its place in the library (the lobby is 0).
        public static int PageOf(int index, int rows = k_Rows) => Mathf.Max(0, index) / PerPage(rows);

        // The library's size in dp for this many apps: the grid, as many rows as a page holds at most,
        // and the tagline under it; with more than a page, the arrows beside the grid and the page
        // dots under it too.
        public static Vector2 Size(int apps, int rows = k_Rows)
        {
            var paged = Pages(apps, rows) > 1;
            var shown = paged ? Mathf.Max(1, rows) : Mathf.Max(1, (apps + k_Columns - 1) / k_Columns);
            var width = k_Columns * k_CellWidth + (k_Columns - 1) * k_Gap + 2f * k_Padding;
            var height = shown * k_CellHeight + (shown - 1) * k_Gap + k_Gap + k_TaglineHeight + 2f * k_Padding;
            if (paged)
            {
                width += 2f * (k_ArrowSize + k_Gap);
                height += k_DotsHeight;
            }
            return new Vector2(width, height);
        }

        // Where an app's cell goes, its middle from the library's middle, in dp: the same place on its
        // own page as the app that many pages before it.
        public static Vector2 CellPosition(int index, int apps, int rows = k_Rows)
        {
            var size = Size(apps, rows);
            var slot = index % PerPage(rows);
            var column = slot % k_Columns;
            var row = slot / k_Columns;
            var left = -size.x / 2f + k_Padding + (Pages(apps, rows) > 1 ? k_ArrowSize + k_Gap : 0f);
            var x = left + k_CellWidth / 2f + column * (k_CellWidth + k_Gap);
            var y = size.y / 2f - k_Padding - k_CellHeight / 2f - row * (k_CellHeight + k_Gap);
            return new Vector2(x, y);
        }

        // The tagline's middle: at the bottom, under the grid (and the page dots).
        public static Vector2 TaglinePosition(Vector2 size) => new(0f, -size.y / 2f + k_Padding + k_TaglineHeight / 2f);

        // How many apps the Android section is laid out for: at least one more than a page, so it has the
        // arrows and the page row from the start, while the list is still loading.
        public static int AndroidLayoutCount(int apps, int rows = k_Rows) => Mathf.Max(apps, PerPage(rows) + 1);

        // Whether the page row shows a dot per page, or else a "3 of 20" label.
        public static bool UsesPageDots(int pages) => pages <= k_MaxPageDots;

        public static string PageLabel(int page, int pages) => $"{page + 1} of {pages}";

        // The rail's size in dp: the two section items, and with the Landscape switch, a divider and it.
        public static Vector2 RailSize(bool withLandscape) =>
            new(k_RailItemWidth + 2f * k_RailPadding,
                (withLandscape ? 3 : 2) * k_RailItemHeight + 2f * k_RailPadding + (withLandscape ? k_RailDividerSpace : 0f));

        // The rail's middle from the library's middle: beside its left edge, k_RailGap away, centered on it.
        public static Vector2 RailPosition(Vector2 librarySize, Vector2 railSize) =>
            new(-librarySize.x / 2f - k_RailGap - railSize.x / 2f, 0f);

        // A rail item's middle from the rail's middle: 0 XR, 1 Android, 2 the Landscape switch (after the
        // divider).
        public static float RailItemY(int item, bool withLandscape)
        {
            var top = RailSize(withLandscape).y / 2f - k_RailPadding - k_RailItemHeight / 2f;
            return top - item * k_RailItemHeight - (item >= 2 ? k_RailDividerSpace : 0f);
        }

        static float RailDividerY(bool withLandscape) => RailItemY(1, withLandscape) - k_RailItemHeight / 2f - k_RailDividerSpace / 2f;

        void Awake()
        {
            m_Rows = Mathf.Clamp(LaunchOptions.GetInt("libraryRows", k_Rows), 1, 4);
            if (m_Settings != null)
                m_Settings.OpenChanged += OnSettingsOpenChanged;
            Layout();
        }

        void OnDestroy()
        {
            if (m_Settings != null)
                m_Settings.OpenChanged -= OnSettingsOpenChanged;
        }

        // The Android section's list and icons arrive while it shows.
        void Update()
        {
            if (!m_Built || m_Section != Section.Android || !IsShown)
                return;
            if (SyncAndroid())
                Arrange();
            if (Time.unscaledTime >= m_NextIconCheck)
            {
                m_NextIconCheck = Time.unscaledTime + k_IconCheckSeconds;
                LoadIcons();
            }
        }

        // The Apps button.
        public void Toggle() => Show(!IsShown);

        public void Show(bool show)
        {
            if (show)
                Build();
            if (show && !IsShown)
            {
                m_ShownAt = Time.unscaledTime;
                m_SectionPages[(int)Section.Xr] = PageOf(RunningIndex(), m_Rows);
                ShowSection(m_Section, log: false);
            }
            M3Fade.SetShown(m_Root.gameObject, show);
            if (m_AppsButton != null)
                m_AppsButton.Selected = show;
            if (show)
            {
                m_Tagline.text = Prompt;
                MarkRunning();
            }
        }

        // The settings panel, opened in front of the user, with the library showing its XR apps: how the
        // lobby greets the user (LobbyFlow).
        public void Open()
        {
            if (m_Settings != null)
                m_Settings.Open();
            if (m_Section != Section.Xr)
            {
                m_Section = Section.Xr;
                if (IsShown)
                    ShowSection(Section.Xr, log: false);
            }
            Show(true);
        }

        // Closed with the panel, so it opens next time as the panel alone.
        void OnSettingsOpenChanged(bool open)
        {
            if (!open && IsShown)
                Show(false);
        }

        // A section of the rail: its tiles, the library sized for them, and the page it was left on.
        public void ShowSection(Section section, bool log = true)
        {
            if (section == Section.Android && !AndroidApps.Supported)
                section = Section.Xr;
            m_Section = section;
            if (m_XrCells != null)
                m_XrCells.gameObject.SetActive(section == Section.Xr);
            if (m_AndroidCells != null)
                m_AndroidCells.gameObject.SetActive(section == Section.Android);
            if (section == Section.Android)
            {
                // Picked up again each time, for apps installed or removed since, and Shizuku started or
                // stopped.
                AndroidApps.Refresh();
                SyncAndroid();
                SetAndroidPrompt(AndroidApps.OpensInXr ? k_XrPrompt : k_FlatPrompt);
            }
            Arrange();
            UpdateRail();
            SetRailPrompt();
            if (log)
                Debug.Log($"[Demos] App library: {(section == Section.Xr ? "XR apps" : $"Android apps ({m_AndroidTiles.Count})")}");
        }

        // What the tagline says while no app is looked at.
        string Prompt => m_Section == Section.Android ? m_AndroidPrompt : k_Prompt;

        void SetAndroidPrompt(string prompt)
        {
            if (prompt == m_AndroidPrompt)
                return;
            m_AndroidPrompt = prompt;
            foreach (var tile in m_AndroidTiles)
                tile.Gaze.Prompt = prompt;
        }

        // The rail's items, shared by the sections, leave the tagline at the section's prompt.
        void SetRailPrompt()
        {
            foreach (var gaze in m_RailGazes)
                gaze.Prompt = Prompt;
        }

        // The section's page count, the library's size for it, and its page showing.
        void Arrange()
        {
            m_Pages = Pages(Mathf.Max(1, AppCount(m_Section)), m_Rows);
            Layout();
            ShowPage(m_SectionPages[(int)m_Section]);
            UpdateMessage();
        }

        int XrCount => m_Demos.Catalog.Demos.Count + 1;

        int AppCount(Section section) => section == Section.Xr ? XrCount : m_AndroidTiles.Count;

        int LayoutCount(Section section) => section == Section.Xr ? XrCount : AndroidLayoutCount(m_AndroidTiles.Count, m_Rows);

        // Sizes the surface and places the tagline, arrows, page row and rail for the section's apps at
        // this many rows a page, which the build only knows the default of. First from Awake, before the
        // panel first opens, so the settings panel is placed below the library's real height.
        void Layout()
        {
            if (m_Demos == null || m_Demos.Catalog == null || m_Root == null)
                return;
            var size = Size(LayoutCount(m_Section), m_Rows);
            m_Root.sizeDelta = size;
            if (m_Grid != null)
                m_Grid.sizeDelta = size;
            if (m_Tagline != null)
            {
                m_Tagline.rectTransform.anchoredPosition = TaglinePosition(size);
                m_Tagline.rectTransform.sizeDelta = new Vector2(size.x - 2f * k_Padding, k_TaglineHeight);
            }
            var gridHeight = m_Rows * k_CellHeight + (m_Rows - 1) * k_Gap;
            var gridY = size.y / 2f - k_Padding - gridHeight / 2f;
            if (m_Previous != null)
            {
                var x = size.x / 2f - k_Padding - k_ArrowSize / 2f;
                ((RectTransform)m_Previous.transform).anchoredPosition = new Vector2(-x, gridY);
                ((RectTransform)m_Next.transform).anchoredPosition = new Vector2(x, gridY);
            }
            if (m_PageLabel != null)
                m_PageLabel.rectTransform.anchoredPosition = new Vector2(0f, DotsY(size));
            if (m_Message != null)
            {
                m_Message.rectTransform.anchoredPosition = new Vector2(0f, gridY);
                m_Message.rectTransform.sizeDelta = new Vector2(size.x - 2f * (k_Padding + k_ArrowSize + k_Gap), gridHeight);
            }
            if (m_Rail != null)
                LayoutRail(size);
        }

        static float DotsY(Vector2 size) => -size.y / 2f + k_Padding + k_TaglineHeight + k_DotsHeight / 2f;

        void Pick(DemoCatalog.Entry entry)
        {
            var title = entry != null ? entry.Title : m_LobbyTitle;
            if (Time.unscaledTime - m_ShownAt < k_PickGuardSeconds)
            {
                Debug.Log($"[Demos] Ignored a pick of {title} {Time.unscaledTime - m_ShownAt:F2} s after the library appeared");
                return;
            }
            if (m_Settings != null)
                m_Settings.Close();
            if (entry == m_Demos.Current)
            {
                Debug.Log($"[Demos] Picked {title} in the app library: already running");
                return;
            }
            Debug.Log($"[Demos] Picked {title} in the app library");
            if (entry == null)
                m_Demos.ExitToLobby();
            else
                m_Demos.Enter(entry);
        }

        // One of the phone's apps: the panel closes and the app opens over CardboardXR, which waits
        // behind it, as it was.
        void PickAndroid(AndroidApps.App app)
        {
            if (Time.unscaledTime - m_ShownAt < k_PickGuardSeconds)
            {
                Debug.Log($"[Demos] Ignored a pick of an Android app {Time.unscaledTime - m_ShownAt:F2} s after the library appeared");
                return;
            }
            if (m_Settings != null)
                m_Settings.Close();
            Debug.Log("[Demos] Picked an Android app in the app library");
            AndroidApps.Open(app);
        }

        void MarkRunning()
        {
            foreach (var (entry, running) in m_Tiles)
                running.SetActive(entry == m_Demos.Current);
        }

        // The running app's place in the library: 0 for the lobby, then the demos in catalog order.
        int RunningIndex()
        {
            var current = m_Demos != null ? m_Demos.Current : null;
            return current == null || m_Demos.Catalog == null ? 0 : m_Demos.Catalog.IndexOf(current.Id) + 1;
        }

        // The lobby's tile, then one per demo, the arrows and page row, and on Android the rail and the
        // Android section; once.
        public void Build()
        {
            if (m_Built || m_Demos == null || m_Demos.Catalog == null)
                return;
            m_Built = true;
            m_XrCells = M3.Group("XR Apps", m_Grid);
            var demos = m_Demos.Catalog.Demos;
            var apps = demos.Count + 1;
            Tile(0, apps, null, m_LobbyTitle, m_LobbyTagline, m_LobbyIcon, m_LobbyTint);
            for (var i = 0; i < demos.Count; i++)
                Tile(i + 1, apps, demos[i], demos[i].Title, demos[i].Tagline, demos[i].Icon, demos[i].Tint);
            if (AndroidApps.Supported)
                m_AndroidCells = M3.Group("Android Apps", m_Grid);
            BuildPaging();
            if (AndroidApps.Supported)
                BuildRail();
            if (Pages(apps, m_Rows) > 1)
                Debug.Log($"[Demos] App library: {apps} apps on {Pages(apps, m_Rows)} pages of {PerPage(m_Rows)}");
            ShowSection(Section.Xr, log: false);
        }

        // The arrows, round and tonal, either side of the grid at its middle height, shown when the
        // section has more than a page; the page label, and on Android the section's message (Layout
        // places them). The dots are made as a section needs them (ShowPageRow).
        void BuildPaging()
        {
            // Listeners added here rather than through M3, which saves them with the scene when built in the
            // editor (as UiPreview does), as a lambda can't be.
            m_Previous = M3.IconButton("Previous Apps", m_Grid, "chevron_left", M3Button.Variant.Tonal, Vector2.zero, null);
            m_Next = M3.IconButton("Next Apps", m_Grid, "chevron_right", M3Button.Variant.Tonal, Vector2.zero, null);
            m_Previous.Button.onClick.AddListener(() => Turn(-1));
            m_Next.Button.onClick.AddListener(() => Turn(1));
            m_PageLabel = M3.Text("Page", m_Grid, "", M3Theme.Type.BodyMedium, Vector2.zero, new Vector2(120f, k_DotsHeight + 6f),
                M3Theme.OnSurfaceVariant);
            m_PageLabel.textWrappingMode = TextWrappingModes.NoWrap;
            if (AndroidApps.Supported)
                m_Message = M3.Text("Message", m_Grid, "", M3Theme.Type.BodyLarge, Vector2.zero, new Vector2(300f, 60f), M3Theme.OnSurfaceVariant);
        }

        // An arrow: the page before or after, stopping at the first and last.
        void Turn(int step)
        {
            var page = Mathf.Clamp(m_Page + step, 0, m_Pages - 1);
            if (page == m_Page)
                return;
            ShowPage(page);
            Debug.Log($"[Demos] App library page {m_Page + 1} of {m_Pages}");
        }

        // Shows that page's tiles only, marks its dot (or says which it is), and dims an arrow that has no
        // page to go to.
        void ShowPage(int page)
        {
            m_Page = Mathf.Clamp(page, 0, m_Pages - 1);
            m_SectionPages[(int)m_Section] = m_Page;
            var perPage = PerPage(m_Rows);
            if (m_Section == Section.Xr)
            {
                for (var i = 0; i < m_Cells.Count; i++)
                    m_Cells[i].SetActive(i / perPage == m_Page);
            }
            else
            {
                for (var i = 0; i < m_AndroidTiles.Count; i++)
                    m_AndroidTiles[i].Cell.SetActive(i / perPage == m_Page);
                m_NextIconCheck = 0f;
            }
            ShowPageRow();
            var paged = m_Pages > 1;
            if (m_Previous != null)
            {
                m_Previous.gameObject.SetActive(paged);
                m_Next.gameObject.SetActive(paged);
                m_Previous.Interactable = m_Page > 0;
                m_Next.Interactable = m_Page < m_Pages - 1;
            }
            // A tile on the page just left may have been the one looked at.
            if (m_Tagline != null)
                m_Tagline.text = Prompt;
        }

        // Under the grid: a dot per page, the one showing in the primary color, or past k_MaxPageDots
        // pages, "3 of 20"; nothing for a single page.
        void ShowPageRow()
        {
            if (m_Root == null)
                return;
            var dots = m_Pages > 1 && UsesPageDots(m_Pages);
            var dotsY = DotsY(m_Root.sizeDelta);
            while (dots && m_Dots.Count < m_Pages)
            {
                m_Dots.Add(M3.Surface($"Page {m_Dots.Count + 1}", m_Grid, Vector2.zero, Vector2.one * k_DotSize, M3Theme.Outline,
                    M3Theme.k_ShapeFull));
            }
            for (var i = 0; i < m_Dots.Count; i++)
            {
                var shown = dots && i < m_Pages;
                m_Dots[i].gameObject.SetActive(shown);
                if (!shown)
                    continue;
                m_Dots[i].rectTransform.anchoredPosition = new Vector2((i - (m_Pages - 1) / 2f) * k_DotSpacing, dotsY);
                m_Dots[i].color = i == m_Page ? M3Theme.Primary : M3Theme.Outline;
            }
            if (m_PageLabel != null)
            {
                var label = m_Pages > 1 && !dots;
                m_PageLabel.gameObject.SetActive(label);
                if (label)
                    m_PageLabel.text = PageLabel(m_Page, m_Pages);
            }
        }

        // An app's cell, the whole of it the target, with a state layer behind it for the gaze.
        RectTransform Cell(RectTransform parent, string title, Vector2 position, UnityAction onPick)
        {
            var cell = M3.Group($"App {title}", parent, position, new Vector2(k_CellWidth, k_CellHeight));
            var state = M3.Surface("State Layer", cell, Vector2.zero, cell.sizeDelta, Color.clear, M3Theme.k_ShapeLarge,
                raycastTarget: true);
            var button = cell.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = state;
            button.onClick.AddListener(onPick);
            cell.gameObject.AddComponent<M3StateLayer>().Initialize(state, button, M3Theme.OnSurface);
            return cell;
        }

        // An app's name under its icon, on one line.
        static void Name(RectTransform cell, string title)
        {
            var name = M3.Text("Name", cell, title, M3Theme.Type.LabelLarge, new Vector2(0f, -36f), new Vector2(k_CellWidth - 8f, 20f));
            name.textWrappingMode = TextWrappingModes.NoWrap;
        }

        // One of the XR apps: an icon on a colored rounded square over its name, and a dot under the name
        // while it's running.
        void Tile(int index, int apps, DemoCatalog.Entry entry, string title, string tagline, string icon, Color tint)
        {
            var cell = Cell(m_XrCells, title, CellPosition(index, apps, m_Rows), () => Pick(entry));
            m_Cells.Add(cell.gameObject);
            var tile = M3.Surface("Icon Tile", cell, new Vector2(0f, 16f), new Vector2(k_IconSize, k_IconSize), tint, k_IconRadius);
            M3.Icon("Icon", tile.rectTransform, icon, 40f, Vector2.zero, Color.white);
            Name(cell, title);
            var running = M3.Surface("Running", cell, new Vector2(0f, -53f), new Vector2(6f, 6f), M3Theme.Primary, M3Theme.k_ShapeFull)
                .gameObject;
            running.SetActive(false);
            cell.gameObject.AddComponent<AppTileGaze>().Initialize(m_Tagline, string.IsNullOrEmpty(tagline) ? title : tagline, k_Prompt);
            m_Tiles.Add((entry, running));
        }

        // ---- The Android section ----

        // The rail: XR and Android, and the Landscape switch (in the Android section), on a translucent
        // pill like the library's own surface.
        void BuildRail()
        {
            var surface = m_Root.GetComponent<Image>();
            var color = surface != null ? surface.color : M3Theme.WithAlpha(M3Theme.SurfaceContainer, 0.6f);
            m_Rail = M3.Surface("Sections", m_Root, Vector2.zero, RailSize(true), color, M3Theme.k_ShapeFull, raycastTarget: true)
                .rectTransform;
            m_XrTab = M3.ToolbarItem("XR", m_Rail, "view_in_ar", "XR", Vector2.zero, k_RailItemWidth, null);
            m_AndroidTab = M3.ToolbarItem("Android", m_Rail, "smartphone", "Android", Vector2.zero, k_RailItemWidth, null);
            m_XrTab.Button.onClick.AddListener(() => ShowSection(Section.Xr));
            m_AndroidTab.Button.onClick.AddListener(() => ShowSection(Section.Android));
            m_RailDivider = M3.Surface("Divider", m_Rail, Vector2.zero, new Vector2(k_RailItemWidth - 2f * k_RailPadding, 1f),
                M3Theme.OutlineVariant, 0f);
            m_LandscapeSwitch = M3.ToolbarItem("Landscape", m_Rail, "aspect_ratio", "Landscape", Vector2.zero, k_RailItemWidth, null);
            m_LandscapeSwitch.Button.onClick.AddListener(ToggleLandscape);
            RailGaze(m_XrTab, "The lobby and the demos");
            RailGaze(m_AndroidTab, "The apps on this phone");
            m_LandscapeGaze = RailGaze(m_LandscapeSwitch, LandscapeTagline());
        }

        AppTileGaze RailGaze(M3Button item, string tagline)
        {
            var gaze = item.gameObject.AddComponent<AppTileGaze>();
            gaze.Initialize(m_Tagline, tagline, k_Prompt);
            m_RailGazes.Add(gaze);
            return gaze;
        }

        static string LandscapeTagline() =>
            AndroidApps.LandscapeEnabled ? "On: apps that can turn open in landscape" : "Off: apps open the way they choose";

        // The section's item selected, and the Landscape switch shown in the Android section.
        void UpdateRail()
        {
            if (m_Rail == null)
                return;
            var android = m_Section == Section.Android;
            m_XrTab.Selected = !android;
            m_AndroidTab.Selected = android;
            m_RailDivider.gameObject.SetActive(android);
            m_LandscapeSwitch.gameObject.SetActive(android);
            m_LandscapeSwitch.Selected = AndroidApps.LandscapeEnabled;
            LayoutRail(m_Root.sizeDelta);
        }

        void LayoutRail(Vector2 librarySize)
        {
            var withLandscape = m_Section == Section.Android;
            var size = RailSize(withLandscape);
            m_Rail.sizeDelta = size;
            m_Rail.anchoredPosition = RailPosition(librarySize, size);
            ((RectTransform)m_XrTab.transform).anchoredPosition = new Vector2(0f, RailItemY(0, withLandscape));
            ((RectTransform)m_AndroidTab.transform).anchoredPosition = new Vector2(0f, RailItemY(1, withLandscape));
            m_RailDivider.rectTransform.anchoredPosition = new Vector2(0f, RailDividerY(withLandscape));
            ((RectTransform)m_LandscapeSwitch.transform).anchoredPosition = new Vector2(0f, RailItemY(2, withLandscape));
        }

        void ToggleLandscape()
        {
            var on = !AndroidApps.LandscapeEnabled;
            AndroidApps.LandscapeEnabled = on;
            m_LandscapeSwitch.Selected = on;
            m_LandscapeGaze.Tagline = LandscapeTagline();
            foreach (var tile in m_AndroidTiles)
                tile.Gaze.Tagline = AndroidApps.Describe(tile.App, on);
        }

        // Takes in a new list of the phone's apps, if there is one with changes: a tile per app, made
        // hidden (ShowPage shows a page's). Returns whether the tiles changed.
        bool SyncAndroid()
        {
            if (m_AndroidCells == null || AndroidApps.Version == m_AndroidVersion)
                return false;
            m_AndroidVersion = AndroidApps.Version;
            var apps = AndroidApps.Apps;
            if (SameApps(apps))
                return false;
            foreach (var tile in m_AndroidTiles)
            {
                tile.Cell.SetActive(false);
                Destroy(tile.Cell);
            }
            m_AndroidTiles.Clear();
            var count = AndroidLayoutCount(apps.Count, m_Rows);
            var landscape = AndroidApps.LandscapeEnabled;
            for (var i = 0; i < apps.Count; i++)
                m_AndroidTiles.Add(MakeAndroidTile(i, count, apps[i], landscape));
            return true;
        }

        bool SameApps(IReadOnlyList<AndroidApps.App> apps)
        {
            if (apps.Count != m_AndroidTiles.Count || apps.Count == 0)
                return false;
            for (var i = 0; i < apps.Count; i++)
            {
                var app = m_AndroidTiles[i].App;
                if (app.Key != apps[i].Key || app.Label != apps[i].Label || app.Orientation != apps[i].Orientation)
                    return false;
            }
            return true;
        }

        // One of the phone's apps: its own icon over its name, a tonal square until the icon has loaded.
        // (Icons from the icon font the app already has: a new one would need the font rebuilt first.)
        AndroidTile MakeAndroidTile(int index, int count, AndroidApps.App app, bool landscape)
        {
            var cell = Cell(m_AndroidCells, app.Label, CellPosition(index, count, m_Rows), () => PickAndroid(app));
            var placeholder = M3.Surface("Icon Tile", cell, new Vector2(0f, 16f), new Vector2(k_IconSize, k_IconSize),
                M3Theme.SurfaceContainerHighest, k_IconRadius);
            M3.Icon("Icon", placeholder.rectTransform, "apps", 40f, Vector2.zero, M3Theme.OnSurfaceVariant);
            var iconObject = new GameObject("App Icon", typeof(RectTransform));
            var iconRect = (RectTransform)iconObject.transform;
            iconRect.SetParent(cell, false);
            iconRect.anchoredPosition = new Vector2(0f, 16f);
            iconRect.sizeDelta = new Vector2(k_IconSize, k_IconSize);
            var icon = iconObject.AddComponent<RawImage>();
            icon.raycastTarget = false;
            icon.enabled = false;
            Name(cell, app.Label);
            var gaze = cell.gameObject.AddComponent<AppTileGaze>();
            gaze.Initialize(m_Tagline, AndroidApps.Describe(app, landscape), m_AndroidPrompt);
            cell.gameObject.SetActive(false);
            return new AndroidTile { App = app, Cell = cell.gameObject, Icon = icon, Placeholder = placeholder.gameObject, Gaze = gaze };
        }

        // The page showing's icons that have loaded since it was last looked at.
        void LoadIcons()
        {
            var perPage = PerPage(m_Rows);
            var end = Mathf.Min(m_AndroidTiles.Count, (m_Page + 1) * perPage);
            for (var i = m_Page * perPage; i < end; i++)
            {
                var tile = m_AndroidTiles[i];
                if (tile.Icon.texture != null)
                    continue;
                var texture = AndroidApps.Icon(tile.App.Key);
                if (texture == null)
                    continue;
                tile.Icon.texture = texture;
                tile.Icon.enabled = true;
                tile.Placeholder.SetActive(false);
            }
        }

        // In the Android section, until there are tiles to show: that the list is coming, or that it's empty.
        void UpdateMessage()
        {
            if (m_Message == null)
                return;
            var text = m_Section != Section.Android ? ""
                : !AndroidApps.Loaded ? "Finding the phone's apps…"
                : m_AndroidTiles.Count == 0 ? "No apps found"
                : "";
            m_Message.text = text;
            m_Message.gameObject.SetActive(text.Length > 0);
        }
    }
}
