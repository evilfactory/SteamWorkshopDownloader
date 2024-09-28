#!/bin/sh

dotnet publish ../SteamWorkshopDownloader/SteamWorkshopDownloader.csproj -c Release --self-contained -r win-x64 -o bin/win-x64 -p:PublishSingleFile=true -p:PublishTrimmed=True -p:TrimMode=Link
dotnet publish ../SteamWorkshopDownloader/SteamWorkshopDownloader.csproj -c Release --self-contained -r linux-x64 -o bin/linux-x64 -p:PublishSingleFile=true -p:PublishTrimmed=True -p:TrimMode=Link
dotnet publish ../SteamWorkshopDownloader/SteamWorkshopDownloader.csproj -c Release --self-contained -r osx-x64 -o bin/osx-x64 -p:PublishSingleFile=true -p:PublishTrimmed=True -p:TrimMode=Link