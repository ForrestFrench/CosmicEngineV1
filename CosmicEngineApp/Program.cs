using CosmicEngine.App.Engine;

// Cosmic Engine
// Entry point only — all logic lives in CosmicEngineApp and the world classes.
// P1: --diagnostic perf-sweep is dispatched here, before any CosmicEngineApp/
// GameWindow is constructed, since PerformanceSweep creates its own fresh
// CosmicEngineApp instance per bounded sub-run.
// Dashboard-Only Launcher Mode: --dashboard-only is dispatched here too, before
// any GameWindow is constructed - DashboardHost starts just the dashboard/
// control server and waits for a scene to be picked before creating one.
if (PerformanceSweep.IsRequested(args))
{
    PerformanceSweep.Run(args);
}
else if (DashboardHost.IsRequested(args))
{
    DashboardHost.Run();
}
else
{
    new CosmicEngineApp().Run(args);
}
