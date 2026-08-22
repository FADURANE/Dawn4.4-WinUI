using System;
using System.Xml.Linq;
using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

/// <summary>
/// Covers the task definition only. Nothing here calls <c>schtasks</c> or touches the registry, so
/// the tests cannot alter the machine's real auto-start state.
/// </summary>
public class StartupRegistrationTests
{
    private const string ExePath = @"C:\Program Files\Dawn4.4 Control\Dawn44.Background.exe";

    [Fact]
    public void BuildStartupTaskXml_IsWellFormedAndDeclaresUtf16()
    {
        var xml = StartupRegistration.BuildStartupTaskXml(ExePath, "--tray");

        // schtasks only accepts a UTF-16 definition, and the declaration has to say so.
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.Equal("Task", ParseBody(xml).Root!.Name.LocalName);
    }

    [Fact]
    public void BuildStartupTaskXml_RunsElevatedWithoutAPrompt()
    {
        var xml = StartupRegistration.BuildStartupTaskXml(ExePath, "--tray");

        // The pair that makes elevated auto-start work at all: an interactive token so the task can
        // own a desktop, at the highest available level so no consent dialog is raised at logon.
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
    }

    [Fact]
    public void BuildStartupTaskXml_CarriesTheRequestedCommandLine()
    {
        var xml = StartupRegistration.BuildStartupTaskXml(ExePath, "--background");

        Assert.Contains($"<Command>{ExePath}</Command>", xml);
        Assert.Contains("<Arguments>--background</Arguments>", xml);
        Assert.Contains(@"<WorkingDirectory>C:\Program Files\Dawn4.4 Control</WorkingDirectory>", xml);
    }

    [Fact]
    public void BuildStartupTaskXml_EscapesMarkupInThePath()
    {
        var xml = StartupRegistration.BuildStartupTaskXml(@"C:\Fish & Chips\app.exe", "--tray");

        Assert.Contains(@"<Command>C:\Fish &amp; Chips\app.exe</Command>", xml);
        Assert.NotNull(ParseBody(xml).Root);
    }

    /// <summary>Task Scheduler rejects the definition outright if these sections are reordered.</summary>
    [Fact]
    public void BuildStartupTaskXml_KeepsTheSectionOrderTaskSchedulerRequires()
    {
        var xml = StartupRegistration.BuildStartupTaskXml(ExePath, "--tray");

        var registration = xml.IndexOf("<RegistrationInfo>", StringComparison.Ordinal);
        var triggers = xml.IndexOf("<Triggers>", StringComparison.Ordinal);
        var principals = xml.IndexOf("<Principals>", StringComparison.Ordinal);
        var settings = xml.IndexOf("<Settings>", StringComparison.Ordinal);
        var actions = xml.IndexOf("<Actions Context=\"Author\">", StringComparison.Ordinal);

        Assert.True(registration >= 0 && actions > 0, "all five sections must be present");
        Assert.True(registration < triggers, "RegistrationInfo must precede Triggers");
        Assert.True(triggers < principals, "Triggers must precede Principals");
        Assert.True(principals < settings, "Principals must precede Settings");
        Assert.True(settings < actions, "Settings must precede Actions");
    }

    /// <summary>
    /// Parses everything after the declaration. The declaration itself is skipped because an
    /// in-memory string is already UTF-16 and the readers disagree about honouring that claim.
    /// </summary>
    private static XDocument ParseBody(string xml)
    {
        var bodyStart = xml.IndexOf("?>", StringComparison.Ordinal) + 2;
        return XDocument.Parse(xml[bodyStart..]);
    }
}
