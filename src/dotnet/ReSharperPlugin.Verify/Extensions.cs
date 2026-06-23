using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.DataContext;
using JetBrains.Application.UI.ActionSystem.ActionsRevised.Menu;
using JetBrains.DocumentModel.DataContext;
using JetBrains.ReSharper.Feature.Services.Actions;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.UnitTestFramework.Actions;
using JetBrains.ReSharper.UnitTestFramework.Criteria;
using JetBrains.ReSharper.UnitTestFramework.Elements;
using JetBrains.ReSharper.UnitTestFramework.Execution;
using JetBrains.Util;
using VerifyTests.ExceptionParsing;

public static class Extensions
{
    public static IActionRequirement GetRequirement(this IDataContext dataContext)
    {
        if (dataContext.GetData(DocumentModelDataConstants.DOCUMENT) == null)
        {
            return CommitAllDocumentsRequirement.TryGetInstance(dataContext);
        }

        return CurrentPsiFileRequirement.FromDataContext(dataContext);
    }

    public static bool HasPendingCompare(this IDataContext context)
    {
        foreach (var (result, _) in context.GetVerifyResults())
        {
            foreach (var file in result.New.Concat(result.NotEqual))
            {
                if (File.Exists(file.Received))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool HasPendingAccept(this IDataContext context)
    {
        foreach (var (result, _) in context.GetVerifyResults())
        {
            foreach (var file in result.New.Concat(result.NotEqual))
            {
                if (File.Exists(file.Received))
                {
                    return true;
                }
            }

            foreach (var file in result.Delete)
            {
                if (File.Exists(file))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static IEnumerable<VirtualFileSystemPath> GetVerifiedFiles(this IDataContext context)
    {
        var session = context.GetData(UnitTestDataConstants.Session.CURRENT);
        if (session == null)
        {
            yield break;
        }

        var elements = context.GetData(UnitTestDataConstants.Elements.IN_CONTEXT)?.Criterion.Evaluate();
        if (elements == null)
        {
            yield break;
        }

        foreach (var element in elements)
        {
            var directory = element.GetProjectFiles()?.FirstOrDefault()?.Location.Parent;
            if (directory == null)
                continue;

            var parent = element.TraverseAcross(x => x.Parent).Last().ShortName;
            var name = element.ShortName
                .Replace("(", "_")
                .Replace(": ", "=")
                .Replace(", ", "_")
                .Replace("\"", string.Empty)
                .Replace(")", string.Empty);
            var verifiedFileName = $"{parent}.{name}.verified";

            foreach (var file in directory.GetChildFiles(verifiedFileName + "*"))
            {
                yield return file;
            }
        }
    }

    public static IEnumerable<(Result, IUnitTestElement)> GetVerifyResults(this IDataContext context)
    {
        var session = context.GetData(UnitTestDataConstants.Session.CURRENT);
        if (session == null)
        {
            yield break;
        }

        var elements = context.GetData(UnitTestDataConstants.Elements.IN_CONTEXT)?.Criterion.Evaluate();
        if (elements == null)
        {
            yield break;
        }

        var resultManager = context.GetComponent<IUnitTestResultManager>();

        foreach (var element in elements)
        {
            var result = resultManager.GetResultData(element, session);
            if (!result.HasVerifyException())
            {
                continue;
            }

            var parsed = result.GetParseResult();
            if (!parsed.Equals(default(Result)))
            {
                yield return (parsed, element);
            }
        }
    }

    private static bool HasVerifyException(this UnitTestResultData result)
    {
        if (result.ExceptionCount == 0)
            return false;

        var info = result.GetExceptionInfo(0);

        // Most adapters (xUnit, NUnit, MSTest) surface the real exception type.
        if (info.Type == "VerifyException")
            return true;

        // Frameworks running on Microsoft.Testing.Platform without a dedicated Rider
        // adapter (e.g. TUnit) come through the generic MTP provider, which reports the
        // failure with a null exception type and only the message text. Fall back to
        // recognising the VerifyException payload from the message itself.
        return TryGetVerifyMessage(info.Message) != null;
    }

    private static Result GetParseResult(this UnitTestResultData result)
    {
        var rawMessage = result.GetExceptionInfo(0).Message!;
        var message = TryGetVerifyMessage(rawMessage) ?? rawMessage;
        try
        {
            return Parser.Parse(message);
        }
        catch (Exception exception)
        {
            MessageBox.ShowError(
                exception.Message +
                "\n\nNote that you might need to rerun tests before your changes take effect.");
            return default;
        }
    }

    private static readonly string[] sectionMarkers = { "New:", "NotEqual:", "Equal:", "Delete:" };

    /// <summary>
    /// Normalises a test failure message down to the raw <c>VerifyException</c> payload that
    /// <see cref="Parser" /> understands, or returns <c>null</c> when the message is not a
    /// Verify failure.
    /// </summary>
    /// <remarks>
    /// This handles the cases where the exception type is unavailable, which happens for test
    /// frameworks that run on Microsoft.Testing.Platform without a dedicated Rider adapter.
    /// TUnit is the notable example: Rider receives the failure through the generic MTP provider,
    /// which loses the exception type (it is reported as <c>null</c>) and may prefix the message
    /// with a category label such as <c>"[Test Failure] "</c>. The remaining text is the raw
    /// <c>VerifyException.Message</c>, which always begins with <c>"Directory:"</c> followed by one
    /// of the section headers.
    /// </remarks>
    private static string TryGetVerifyMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return null;

        var candidate = message.TrimStart();

        // Strip a leading "[label] " prefix added by TUnit on Microsoft.Testing.Platform.
        if (candidate.StartsWith("["))
        {
            var closing = candidate.IndexOf(']');
            if (closing > 0)
            {
                candidate = candidate.Substring(closing + 1).TrimStart();
            }
        }

        if (LooksLikeVerifyPayload(candidate))
            return candidate;

        // MSTest prepends "Test method ..." on the first line, with "VerifyException" and the
        // payload starting on a later line.
        var index = message.IndexOf("VerifyException", StringComparison.Ordinal);
        return index >= 0 ? message.Substring(index) : null;
    }

    private static bool LooksLikeVerifyPayload(string candidate)
    {
        if (candidate.StartsWith("VerifyException"))
            return true;

        if (!candidate.StartsWith("Directory:"))
            return false;

        // Guard against unrelated failures that merely start with "Directory:" by requiring at
        // least one of the section headers a VerifyException always contains.
        foreach (var marker in sectionMarkers)
        {
            if (candidate.Contains(marker))
                return true;
        }

        return false;
    }
}
