#!/usr/bin/env pwsh

# WeatherWall Development Clean-Build Script
# Cleans all build artifacts, personal config, and logs
# Run this before committing to ensure clean repository state

Write-Host "🧹 WeatherWall Clean-Build Script" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan
Write-Host ""

# Clean personal configuration
Write-Host "🔍 Removing personal configuration..." -ForegroundColor Yellow
if (Test-Path "config.json") {
    Remove-Item "config.json" -Force
    Write-Host "   ✓ Removed config.json" -ForegroundColor Green
}

# Clean logs
Write-Host "🔍 Removing log files..." -ForegroundColor Yellow
Get-ChildItem -Path "." -Filter "*.log" -Recurse | ForEach-Object {
    Remove-Item $_.FullName -Force
    Write-Host "   ✓ Removed $($_.Name)" -ForegroundColor Green
}

# Clean build artifacts
Write-Host "🔍 Removing build artifacts..." -ForegroundColor Yellow
if (Test-Path "bin") {
    Remove-Item "bin" -Recurse -Force
    Write-Host "   ✓ Removed bin/" -ForegroundColor Green
}

if (Test-Path "obj") {
    Remove-Item "obj" -Recurse -Force
    Write-Host "   ✓ Removed obj/" -ForegroundColor Green
}

# Clean debug database files
Get-ChildItem -Path "." -Filter "*.pdb" -Recurse | ForEach-Object {
    Remove-Item $_.FullName -Force
    Write-Host "   ✓ Removed $($_.Name)" -ForegroundColor Green
}

# Clean temp files
Get-ChildItem -Path "." -Filter "*.tmp" -Recurse | ForEach-Object {
    Remove-Item $_.FullName -Force
    Write-Host "   ✓ Removed $($_.Name)" -ForegroundColor Green
}

Write-Host ""
Write-Host "🏗️  Building clean release..." -ForegroundColor Cyan
dotnet clean -c Release
dotnet build -c Release

Write-Host ""
Write-Host "✅ Clean build complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Ready for:" -ForegroundColor Cyan
Write-Host "  • Commit to repository" -ForegroundColor Gray
Write-Host "  • Release build: dotnet publish -c Release -r win-x64 --self-contained" -ForegroundColor Gray
Write-Host ""
