using System;
using System.IO;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Core.Services
{
    public sealed class LanguageDetectionService : ILanguageDetectionService
    {
        public string GetEditorLanguageName(string filePath)
        {
            string name = Path.GetFileName(filePath);
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
            if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)) return "makefile";

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".md" => "markdown",
                ".markdown" => "markdown",
                ".html" => "html",
                ".htm" => "html",
                ".tex" => "latex",
                ".diff" => "diff",
                ".cs" => "csharp",
                ".fs" => "fsharp",
                ".vb" => "vb",
                ".vbs" => "vbscript",
                ".js" => "javascript",
                ".jsx" => "javascript",
                ".mjs" => "javascript",
                ".cjs" => "javascript",
                ".ts" => "typescript",
                ".tsx" => "typescript",
                ".mts" => "typescript",
                ".cts" => "typescript",
                ".css" => "css",
                ".scss" => "scss",
                ".less" => "less",
                ".json" => "json",
                ".jsonc" => "json",
                ".py" => "python",
                ".cpp" => "cpp",
                ".cxx" => "cpp",
                ".cc" => "cpp",
                ".c" => "cpp",
                ".h" => "cpp",
                ".hpp" => "cpp",
                ".xml" => "xml",
                ".xaml" => "xml",
                ".resw" => "xml",
                ".appxmanifest" => "xml",
                ".csproj" => "xml",
                ".manifest" => "xml",
                ".sql" => "sql",
                ".sh" => "shell",
                ".bash" => "shell",
                ".zsh" => "shell",
                ".ps1" => "powershell",
                ".psm1" => "powershell",
                ".psd1" => "powershell",
                ".rs" => "rust",
                ".go" => "go",
                ".java" => "java",
                ".kt" => "kotlin",
                ".kts" => "kotlin",
                ".swift" => "swift",
                ".php" => "php",
                ".rb" => "ruby",
                ".dart" => "dart",
                ".lua" => "lua",
                ".r" => "r",
                ".rprofile" => "r",
                ".dockerfile" => "dockerfile",
                ".toml" => "toml",
                ".ini" => "ini",
                ".yml" => "yaml",
                ".yaml" => "yaml",
                ".reg" => "reg",
                ".csv" => "csv",
                _ => "plaintext"
            };
        }

        public string DetectLanguageFromContent(string text, string defaultLanguage = "plaintext")
        {
            if (string.IsNullOrWhiteSpace(text)) return defaultLanguage;

            string sample = text.Trim();
            if (sample.Length > 2000) sample = sample.Substring(0, 2000);

            if (sample.StartsWith("{") && sample.EndsWith("}") && sample.Contains("\"")) return "json";
            if (sample.StartsWith("[") && sample.EndsWith("]") && sample.Contains("{\"")) return "json";
            if (sample.StartsWith("diff --git") || sample.Contains("\n@@ ")) return "diff";

            if (sample.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase) ||
                (sample.Contains("[HKEY_LOCAL_MACHINE") || sample.Contains("[HKEY_CURRENT_USER") ||
                 sample.Contains("[HKEY_CLASSES_ROOT") || sample.Contains("[HKEY_USERS") ||
                 sample.Contains("[HKEY_CURRENT_CONFIG"))) return "reg";

            if (sample.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                sample.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                sample.Contains("<head", StringComparison.OrdinalIgnoreCase) ||
                sample.Contains("<body", StringComparison.OrdinalIgnoreCase)) return "html";

            if (sample.Contains("\\documentclass") ||
                sample.Contains("\\begin{document}") ||
                sample.Contains("\\begin{align}") ||
                sample.Contains("$$\n") ||
                sample.Contains("\\frac{")) return "latex";

            if (sample.Contains("\n# ") || sample.StartsWith("# ") ||
                sample.Contains("## ") ||
                sample.Contains("```") ||
                sample.Contains("- [ ] ") ||
                sample.Contains("**")) return "markdown";

            if (sample.Contains("using System;") ||
                sample.Contains("namespace ") ||
                (sample.Contains("public class ") && sample.Contains("void Main")) ||
                sample.Contains("Console.WriteLine(")) return "csharp";

            if (sample.Contains("#include <iostream>") ||
                sample.Contains("std::cout") ||
                sample.Contains("int main()")) return "cpp";

            if (sample.Contains("public class ") && sample.Contains("public static void main") ||
                sample.Contains("System.out.println(")) return "java";

            if (sample.Contains("import os") ||
                (sample.Contains("def ") && sample.Contains(":")) ||
                (sample.Contains("print(") && sample.Contains("if __name__ == ")) ||
                sample.Contains("elif ")) return "python";

            if ((sample.Contains("const ") && sample.Contains(" = require(")) ||
                (sample.Contains("import ") && sample.Contains(" from ")) ||
                sample.Contains("console.log(") ||
                sample.Contains("document.getElementById(")) return "javascript";

            if (sample.Contains("interface ") && sample.Contains(": ") ||
                sample.Contains("type ") && sample.Contains(" = ") ||
                sample.Contains("React.") ||
                sample.Contains("useState(")) return "typescript";

            if (sample.Contains("fn main()") ||
                sample.Contains("let mut ") ||
                sample.Contains("pub struct ") ||
                sample.Contains("impl ") ||
                sample.Contains("use std::")) return "rust";

            if (sample.Contains("package main") ||
                sample.Contains("import (") ||
                sample.Contains("func main()")) return "go";

            if (sample.Contains("fun main(") ||
                sample.Contains("val ") ||
                sample.Contains("data class ")) return "kotlin";

            if (sample.Contains("import SwiftUI") ||
                sample.Contains("let ") && sample.Contains("func ")) return "swift";

            if (sample.Contains("<?php") ||
                sample.Contains("echo $") ||
                sample.Contains("function ") && sample.Contains("$")) return "php";

            if (sample.StartsWith("#!/usr/bin/env ruby") ||
                sample.Contains("puts ") ||
                sample.Contains("def ") && sample.Contains("end")) return "ruby";

            if (sample.Contains("library(") ||
                sample.Contains("<-") && sample.Contains("function(") ||
                sample.Contains("data.frame(") ||
                sample.Contains("ggplot(")) return "r";

            if (sample.Contains("local function ") ||
                sample.Contains("function ") && sample.Contains(" end")) return "lua";

            if (sample.Contains("FROM ") && sample.Contains("RUN ", StringComparison.OrdinalIgnoreCase)) return "dockerfile";

            if (sample.Contains("SELECT ", StringComparison.OrdinalIgnoreCase) &&
                sample.Contains("FROM ", StringComparison.OrdinalIgnoreCase)) return "sql";

            if (sample.Contains("body {") ||
                sample.Contains(".class {") ||
                sample.Contains("#id {") ||
                sample.Contains("margin:") ||
                sample.Contains("padding:")) return "css";

            if (sample.Contains("---") &&
                (sample.Contains("version:") || sample.Contains("name:") || sample.Contains("author:"))) return "yaml";

            if (sample.Contains("[package]") || sample.Contains("[dependencies]")) return "toml";

            if (sample.StartsWith("#!/bin/bash") ||
                sample.StartsWith("#!/bin/sh") ||
                sample.StartsWith("#!/usr/bin/env bash") ||
                sample.Contains("echo ") ||
                sample.Contains("export ")) return "shell";

            if (sample.Contains("param(") ||
                sample.Contains("Write-Host") ||
                sample.Contains("Get-ChildItem")) return "powershell";

            if (sample.Contains("Option Explicit") ||
                sample.Contains("WScript.Echo") ||
                sample.Contains("CreateObject(\"") ||
                sample.Contains("MsgBox ") ||
                sample.Contains("On Error Resume Next")) return "vbscript";

            return defaultLanguage;
        }
    }
}
