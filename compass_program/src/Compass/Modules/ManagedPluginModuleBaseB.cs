namespace Compass.Modules;

/// <summary>
/// Temporary compatibility shim that preserves support for modules still inheriting from
/// the legacy <c>ManagedPluginModuleBaseB</c> type. The new implementation now lives in
/// <see cref="ManagedPluginModuleBase"/> so we simply derive from it without adding
/// additional behavior.
/// </summary>
public abstract class ManagedPluginModuleBaseB : ManagedPluginModuleBase
{
}
