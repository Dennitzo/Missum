#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"

// Called exclusively on the editor's game thread.
TSharedPtr<FJsonObject> MissumExecuteCommand(const FString& Command, const TSharedPtr<FJsonObject>& Args, const FString& InstanceId);
