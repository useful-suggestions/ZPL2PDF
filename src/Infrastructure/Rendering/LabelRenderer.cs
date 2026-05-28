using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaryKits.Zpl.Label;
using BinaryKits.Zpl.Viewer;
using BinaryKits.Zpl.Viewer.ElementDrawers;
using SkiaSharp;
using ZPL2PDF.Domain.Services;

namespace ZPL2PDF {
    /// <summary>
    /// Responsible for processing labels, generating images in memory, and returning image data.
    /// </summary>
    public class LabelRenderer : ILabelRenderer {
        private readonly IPrinterStorage _printerStorage;
        private readonly ZplAnalyzer _analyzer;
        private readonly ZplElementDrawer _drawer;
        private readonly double _labelWidthMm;
        private readonly double _labelHeightMm;
        private readonly int _printDpi;
        private readonly double _labelWidthInput;
        private readonly double _labelHeightInput;
        private readonly string _labelUnitInput;
        private readonly int _labelDpi;
        private readonly string? _fontsDirectory;
        private readonly IReadOnlyList<(string Id, string Path)>? _fontMappings;

        private const double InchesToMm = 25.4;
        private const double CmToMm = 10.0;
        private const double DpiToDpmm = 25.4;

        /// <summary>
        /// Creates DrawerOptions with high quality settings. Optional custom font loader for ^A0N, ^AAN, ^ABN, etc.
        /// </summary>
        private DrawerOptions CreateDrawerOptions() {
            var options = new DrawerOptions {
                RenderFormat = SKEncodedImageFormat.Png,
                RenderQuality = 100,
                PdfOutput = false,
                OpaqueBackground = false
            };
            if (_fontsDirectory != null || (_fontMappings != null && _fontMappings.Count > 0)) {
                // BinaryKits resolves text typefaces via options.FontManager.FontLoader; the legacy
                // options.FontLoader property is [Obsolete] and ignored during rendering. Wrap the
                // existing loader so unmapped font ids keep BinaryKits' default behavior.
                var fallback = options.FontManager.FontLoader;
                options.FontManager.FontLoader = CreateFontLoader(fallback);
            }
            return options;
        }

        /// <summary>
        /// Builds a delegate that resolves ZPL font ID (0, A, B, ...) to SKTypeface from --fonts-dir and --font mappings.
        /// </summary>
        private Func<string, SKTypeface?> CreateFontLoader(Func<string, SKTypeface?>? fallback) {
            var fontsDir = _fontsDirectory ?? string.Empty;
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_fontMappings != null) {
                foreach (var (id, path) in _fontMappings) {
                    if (string.IsNullOrEmpty(id)) continue;
                    var resolved = ResolveFontPath(path, fontsDir);
                    mappings[id.Trim()] = resolved;
                }
            }
            // Opt-in resolution logging (stderr, so it never corrupts stdout PDF output).
            // Set ZPL2PDF_FONT_DEBUG=1 to see, per font id, whether it loaded from file or fell back.
            bool debug = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZPL2PDF_FONT_DEBUG"));
            var logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Log(string id, string outcome) {
                if (!debug || !logged.Add(id)) return; // one line per unique font id
                Console.Error.WriteLine($"[ZPL2PDF][font] id='{id}' -> {outcome}");
            }
            SKTypeface? Fallback(string id) => fallback != null ? fallback(id) : SKTypeface.Default;

            return fontId => {
                if (string.IsNullOrEmpty(fontId)) {
                    Log("(empty)", "fallback (empty font id)");
                    return Fallback(fontId);
                }
                var key = fontId.Trim();
                if (mappings.TryGetValue(key, out var path) && File.Exists(path)) {
                    try {
                        var tf = SKTypeface.FromFile(path);
                        if (tf != null) {
                            Log(key, $"FromFile (mapping): {path} [family={tf.FamilyName}]");
                            return tf;
                        }
                        Log(key, $"SKTypeface.Default (mapping file unreadable: {path})");
                        return SKTypeface.Default;
                    } catch (Exception ex) {
                        Log(key, $"SKTypeface.Default (mapping FromFile failed: {path} - {ex.GetType().Name})");
                        return SKTypeface.Default;
                    }
                }
                if (!string.IsNullOrWhiteSpace(fontsDir)) {
                    var byName = Path.Combine(fontsDir, key + ".ttf");
                    if (File.Exists(byName)) {
                        try {
                            var tf = SKTypeface.FromFile(byName);
                            if (tf != null) { Log(key, $"FromFile (fonts-dir): {byName} [family={tf.FamilyName}]"); return tf; }
                        } catch (Exception ex) { Log(key, $"fonts-dir FromFile failed: {byName} - {ex.GetType().Name}"); }
                    }
                    var byNameOtf = Path.Combine(fontsDir, key + ".otf");
                    if (File.Exists(byNameOtf)) {
                        try {
                            var tf = SKTypeface.FromFile(byNameOtf);
                            if (tf != null) { Log(key, $"FromFile (fonts-dir): {byNameOtf} [family={tf.FamilyName}]"); return tf; }
                        } catch (Exception ex) { Log(key, $"fonts-dir FromFile failed: {byNameOtf} - {ex.GetType().Name}"); }
                    }
                }
                Log(key, "fallback to BinaryKits default (no mapping/file matched)");
                return Fallback(key);
            };
        }

        /// <summary>
        /// Resolves font path declared in --font mapping.
        /// Supports absolute paths and relative paths (with or without nested folders) against --fonts-dir.
        /// </summary>
        private static string ResolveFontPath(string declaredPath, string fontsDir) {
            if (string.IsNullOrWhiteSpace(declaredPath)) {
                return declaredPath;
            }

            if (Path.IsPathRooted(declaredPath) || string.IsNullOrWhiteSpace(fontsDir)) {
                return declaredPath;
            }

            // Keep nested relative path first (e.g. custom/arial.ttf), then fallback to only filename for compatibility.
            var combined = Path.Combine(fontsDir, declaredPath);
            if (File.Exists(combined)) {
                return combined;
            }

            return Path.Combine(fontsDir, Path.GetFileName(declaredPath));
        }

        /// <summary>
        /// Initializes a new instance of the LabelRenderer class, setting up the necessary dependencies for rendering labels into images.
        /// </summary>
        public LabelRenderer(double labelWidth, double labelHeight, int printDpi, string unit,
            string? fontsDirectory = null,
            IReadOnlyList<(string Id, string Path)>? fontMappings = null) {
            _fontsDirectory = fontsDirectory;
            _fontMappings = fontMappings;
            _printerStorage = new PrinterStorage();
            _analyzer = new ZplAnalyzer(_printerStorage);

            var drawerOptions = CreateDrawerOptions();
            _drawer = new ZplElementDrawer(_printerStorage, drawerOptions);

            // Convert width and height to millimeters based on the unit
            switch (unit) {
                case "in":
                    _labelWidthMm = labelWidth * InchesToMm;
                    _labelHeightMm = labelHeight * InchesToMm;
                    break;
                case "cm":
                    _labelWidthMm = labelWidth * CmToMm;
                    _labelHeightMm = labelHeight * CmToMm;
                    break;
                case "mm":
                    _labelWidthMm = labelWidth;
                    _labelHeightMm = labelHeight;
                    break;
                default:
                    _labelWidthMm = 60;   // 60 mm
                    _labelHeightMm = 120;  // 120 mm
                    break;
            }

            // Store DPI (will be converted to DPMM when rendering)
            _printDpi = printDpi;
            _labelWidthInput = labelWidth;
            _labelHeightInput = labelHeight;
            _labelUnitInput = unit;
            _labelDpi = printDpi;
        }

        /// <summary>
        /// Initializes a new instance of the LabelRenderer class using LabelDimensions (for daemon mode).
        /// </summary>
        public LabelRenderer(LabelDimensions dimensions,
            string? fontsDirectory = null,
            IReadOnlyList<(string Id, string Path)>? fontMappings = null) {
            _fontsDirectory = fontsDirectory;
            _fontMappings = fontMappings;
            _printerStorage = new PrinterStorage();
            _analyzer = new ZplAnalyzer(_printerStorage);

            var drawerOptions = CreateDrawerOptions();
            _drawer = new ZplElementDrawer(_printerStorage, drawerOptions);

            _labelWidthMm = dimensions.WidthMm;
            _labelHeightMm = dimensions.HeightMm;
            _printDpi = dimensions.Dpi;
            _labelWidthInput = dimensions.WidthMm;
            _labelHeightInput = dimensions.HeightMm;
            _labelUnitInput = "mm";
            _labelDpi = dimensions.Dpi;
        }

        /// <summary>
        /// Processes a list of ZPL labels and returns a list of images (in byte[]).
        /// </summary>
        /// <param name="labels">List of ZPL labels.</param>
        /// <returns>List of images in byte arrays.</returns>
        /// <exception cref="ArgumentNullException">Thrown when labels is null.</exception>
        public List<byte[]> RenderLabels(List<string> labels) {
            if (labels == null) {
                throw new ArgumentNullException(nameof(labels));
            }

            var images = new List<byte[]>();
            
            // Convert DPI to DPMM for the drawer
            int dpmm = (int)Math.Round(_printDpi / DpiToDpmm);
            
            for (int i = 0; i < labels.Count; i++) {
                var labelText = labels[i];
                
                // Create a fresh PrinterStorage and Analyzer for each label to avoid state pollution
                // This ensures that graphics from one label don't affect another
                var printerStorage = new PrinterStorage();
                var analyzer = new ZplAnalyzer(printerStorage);
                
                // Process the complete label (graphics + label together)
                // The ZplAnalyzer will process graphics first and load them into PrinterStorage
                // Then it will process the label which can reference those graphics
                var analyzeInfo = analyzer.Analyze(labelText);
                
                // Process all LabelInfos - the ZplAnalyzer should only generate one per ^XA...^XZ
                if (analyzeInfo.LabelInfos != null) {
                    foreach (var labelInfo in analyzeInfo.LabelInfos) {
                        // Create a fresh drawer for this label to use the correct PrinterStorage
                        var drawerOptions = CreateDrawerOptions();
                        var drawer = new ZplElementDrawer(printerStorage, drawerOptions);
                        
                        // Use DPMM for Draw (BinaryKits library expects DPMM)
                        // save the values in .txt file to debug the label dimensions and DPI used for rendering
                        File.WriteAllText("label_dimensions.txt", $"Width: {_labelWidthMm}, Height: {_labelHeightMm}, DPI: {_labelDpi}, DPMM: {dpmm}");
                        byte[] imageData = drawer.Draw(labelInfo.ZplElements, _labelWidthMm, _labelHeightMm, dpmm);
                        images.Add(imageData);
                    }
                }
            }
            return images;
        }

        /// <inheritdoc />
        public (double width, double height, string unit, int dpi) GetDimensions()
        {
            return (_labelWidthInput, _labelHeightInput, _labelUnitInput, _labelDpi);
        }
    }
}