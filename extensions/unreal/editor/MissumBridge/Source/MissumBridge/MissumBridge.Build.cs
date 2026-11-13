using UnrealBuildTool;

public class MissumBridge : ModuleRules
{
    public MissumBridge(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PrivateDependencyModuleNames.AddRange(new[] {
            "Core", "CoreUObject", "Engine", "UnrealEd", "Sockets", "Networking",
            "Json", "JsonUtilities", "AssetRegistry", "BlueprintGraph", "Kismet",
            "KismetCompiler", "ImageWrapper", "RenderCore", "RHI", "EngineSettings"
        });
    }
}
