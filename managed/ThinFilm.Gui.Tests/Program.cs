// Program.cs — the GUI suite, run as its own executable.
//
// Separate from ThinFilm.Native.Tests so that the main suite keeps its property of referencing no
// UI at all. Same harness, same output shape, so the two totals read the same way.

using Avalonia.Headless;
using ThinFilm.Gui.Tests;
using ThinFilm.Native.Tests;

Console.WriteLine();
Console.WriteLine("=== ThinFilm.Gui: the window, headless ===");
Console.WriteLine();

// A test run must never read or write the profile of the person running it: ThinFilm's own
// files (the material database, the settings) go to a fresh temporary folder instead.
var home = Path.Combine(Path.GetTempPath(), "thinfilm-gui-tests-" + Environment.ProcessId);
Environment.SetEnvironmentVariable("THINFILM_HOME", home);

var h = new Harness();

// One session for the whole run: it starts an Avalonia application on its own dispatcher thread,
// and starting a second in the same process is not supported.
using var session = HeadlessUnitTestSession.GetOrStartForAssembly(
    typeof(WindowTests).Assembly);

WindowTests.TheWindowOpensWithItsControls(h, session);        Console.WriteLine();
WindowTests.ClickingOpenFillsTheTables(h, session);           Console.WriteLine();
WindowTests.EachOptimisationHasAWindowOfItsOwn(h, session);  Console.WriteLine();
WindowTests.TheNeedleAdviceIsGone(h, session);                Console.WriteLine();
WindowTests.TheMeritFunctionIsBuiltWithTheNewButtons(h, session); Console.WriteLine();
WindowTests.ThePlotActuallyPaintsACurve(h, session);          Console.WriteLine();
WindowTests.FailuresReachTheStatusLineRatherThanThrowing(h, session); Console.WriteLine();
WindowTests.TheTablesFitTheirPane(h, session);                Console.WriteLine();
WindowTests.EachTabShowsItsPanel(h, session);                 Console.WriteLine();
WindowTests.TheMenuReachesWhatTheButtonsDo(h, session);        Console.WriteLine();
WindowTests.TheHelpBarExplainsTheSelectedRow(h, session);      Console.WriteLine();
WindowTests.AMeritCellIsEditedByTyping(h, session);            Console.WriteLine();
WindowTests.LayersAreEditedWithTheMouse(h, session);           Console.WriteLine();
WindowTests.NewDesignIsMadeFromTheDialog(h, session);          Console.WriteLine();
WindowTests.MaterialsAreCopiedInFromTheDatabase(h, session);   Console.WriteLine();
WindowTests.ACatalogueGlassIsChosenAndShownReadOnly(h, session); Console.WriteLine();
WindowTests.OneSelectionDrivesTheSourceButtons(h, session);    Console.WriteLine();
WindowTests.AConstantMaterialIsReplacedByARealOne(h, session);  Console.WriteLine();
WindowTests.DispersionIsEditedInItsOwnDialog(h, session);      Console.WriteLine();
WindowTests.SystemParametersSetTheWavelengthUnit(h, session);       Console.WriteLine();
WindowTests.SystemParametersSetTheBackSurfaceAndTheAngles(h, session); Console.WriteLine();
WindowTests.TheSpectrumTabPlotsAgainstAngle(h, session);        Console.WriteLine();
WindowTests.SpectrumQuantitiesAreTickedInADialog(h, session); Console.WriteLine();
WindowTests.LayerUnitsAndPickupsAreSetInTheDialogs(h, session); Console.WriteLine();
WindowTests.ApplyInSystemParametersPutsEveryLengthInMicrons(h, session); Console.WriteLine();
WindowTests.ADesignStringFillsTheLayerTable(h, session); Console.WriteLine();
WindowTests.TheStackMenuCouplesDecouplesAndEmpties(h, session); Console.WriteLine();
WindowTests.TheStackMenuReversesTheLayersAndTheMedia(h, session); Console.WriteLine();
WindowTests.TheMeritFunctionMenuHasItsFileCommands(h, session); Console.WriteLine();
WindowTests.BoundsAreSetInLayerPropertiesAndForARange(h, session); Console.WriteLine();
WindowTests.TheUnconstrainedMaximumIsSetInSystemParameters(h, session); Console.WriteLine();
WindowTests.TheDesignReportGoesThroughItsDialog(h, session);   Console.WriteLine();
WindowTests.ADesignNamedAtLaunchOpens(h, session);           Console.WriteLine();
WindowTests.TheSampleBrowserFindsAndOpens(h, session);       Console.WriteLine();
WindowTests.ZemaxCoatingsGoThroughTheirDialogs(h, session);   Console.WriteLine();
WindowTests.OptilandCoatingsGoThroughTheirDialog(h, session);  Console.WriteLine();
WindowTests.TheDescriptionIsAboveTheLayers(h, session);        Console.WriteLine();
WindowTests.ClosingAChangedDesignAsksToSaveIt(h, session);    Console.WriteLine();
WindowTests.RefineRunsInAWindowOfItsOwn(h, session);          Console.WriteLine();

Console.WriteLine($"=== {h.Checks} checks, {h.Failures} failures ===");
Console.WriteLine();
try { Directory.Delete(home, recursive: true); } catch (IOException) { }
return h.Failures == 0 ? 0 : 1;
