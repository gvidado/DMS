using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        internal static IEnumerable<SequenceStep> SequenceJumpSteps(IEnumerable<SequenceStep> steps)
        {foreach(var step in steps){yield return step;if(step.NextSteps!=null)foreach(var next in SequenceJumpSteps(step.NextSteps))yield return next;}}
        private void TransitionSequence(SequenceStep step)
        {
            foreach(var command in SequenceJumpSteps(step.NextSteps).Where(s=>s.Code.Length>0))
            {if(SequenceCommandSignature(command,ResolveCommandCode(command.Code))!=command.CommandSignature){StopSequenceCore("CALL目标所用指令定义已变化，请重新启动序列");return;}}
            foreach(var row in sequenceRows.Where(r=>!r.Terminal && r.Step.Row.Keyword=="TEXT"))
            CompleteSequenceText(row);
            string next=step.NextDefinition.Code;
            sequenceReason=SequenceCompletionStatus()+"；CALL转至"+next;
            SequenceAudit("结束",null,SequenceCompletionStatus(),sequenceReason);
            sequenceRunning=false;sequenceClock.Stop();Interlocked.Increment(ref CurrentSequence.Generation);
            // A jump starts a fresh run: new archive, new clock and no inherited T0.
            StartSequenceCall(step);
        }
    }
}
