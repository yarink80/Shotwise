using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;

namespace Shotwise.Editor;

// Step badges (EDT-06 Step). Counters live on the page (Page.StepCounters, persisted in the document).
// Renumber policy: values are NOT renumbered automatically when a step is deleted or reordered — the remaining badges
// keep their values and the counter keeps advancing (a deliberate, predictable choice; undo restores both the badge
// and the counter). Use RestartStepSequence / EditStepValue / RenumberSteps to fix numbering explicitly.
public sealed partial class EditorSession
{
    /// <summary>Next value the Step tool will place for <paramref name="sequence"/> on the active page.</summary>
    public int NextStepValue(StepSequence sequence) => Math.Max(1, ActivePage.StepCounters.Next(sequence));

    /// <summary>Adds a step badge with the next value of its sequence and advances the counter (one undo step).</summary>
    internal void PlaceStep(StepAnnotation step, IEditCommand? extra = null)
    {
        var page = ActivePage;
        step.Value = NextStepValue(step.Sequence);
        var counters = page.StepCounters.With(step.Sequence, step.Value + 1);
        var cmds = new List<IEditCommand>
        {
            new AddAnnotationCommand(page.Id, step, label: "Add step"),
            new SetStepCountersCommand(page.Id, counters, "Add step"),
        };
        if (extra is not null) cmds.Add(extra);
        ExecuteComposite("Add step", cmds);
        Selection.Select(step.Id);
    }

    /// <summary>"Restart Sequence": the next badge of <paramref name="sequence"/> gets <paramref name="startAt"/>.</summary>
    public void RestartStepSequence(StepSequence sequence, int startAt = 1)
    {
        var page = ActivePage;
        var next = page.StepCounters.With(sequence, Math.Max(1, startAt));
        if (next == page.StepCounters) return;
        Execute(new SetStepCountersCommand(page.Id, next, "Restart sequence"));
    }

    /// <summary>"Edit Value…": sets a badge's value; the sequence continues after it (next = value + 1).</summary>
    public void EditStepValue(Guid stepId, int value)
    {
        if (ActivePage.Find(stepId) is not StepAnnotation step) return;
        value = Math.Max(1, value);
        var page = ActivePage;
        ExecuteComposite("Edit step value", new IEditCommand[]
        {
            ModifyAnnotationsCommand.Create(page, new[] { stepId }, a => ((StepAnnotation)a).Value = value, "Edit step value"),
            new SetStepCountersCommand(page.Id, page.StepCounters.With(step.Sequence, value + 1), "Edit step value"),
        });
    }

    /// <summary>Explicit renumbering: badges of <paramref name="sequence"/> get 1..n in z-order (bottom = first).</summary>
    public void RenumberSteps(StepSequence sequence)
    {
        var page = ActivePage;
        var steps = page.Annotations.OfType<StepAnnotation>().Where(s => s.Sequence == sequence).ToList();
        if (steps.Count == 0) return;
        var values = steps.Select((s, i) => (s.Id, Value: i + 1)).ToDictionary(t => t.Id, t => t.Value);
        ExecuteComposite("Renumber steps", new IEditCommand[]
        {
            ModifyAnnotationsCommand.Create(page, values.Keys, a => ((StepAnnotation)a).Value = values[a.Id], "Renumber steps"),
            new SetStepCountersCommand(page.Id, page.StepCounters.With(sequence, steps.Count + 1), "Renumber steps"),
        });
    }
}
