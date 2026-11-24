# Agents Propose, Tools Decide

### An introduction to verifying agent-written .NET code

##### In this book

Why verification matters when an agent writes your code The verification ladder, one check at a time Making the compiler rule out bad states Gates, evidence, and property-based tests The agent loop, and where to go next

-----

## Why verification matters

A coding agent can write a refund method in seconds. It compiles, and the example tests pass. Then it ships, and it refunds money on a payment that was never captured. Nothing about that code looked wrong. The tests only checked the inputs someone thought of, and the review only caught what a tired reader happened to notice. You can't fix this by asking the agent to be more careful. Asking a second agent to review the first one doesn't help much either. An agent can sound confident and still be wrong, so it makes a poor judge of its own work.

#### The core idea

Split the work three ways:

**The agent proposes. It writes the change, picks what to test, runs the tools, and reads their output. The tools decide. A deterministic tool gives the same yes or no answer every time. Only tool output**

can block a merge.

**You own the rules. You decide what "correct" means, set the thresholds, and press the merge**

button. The trick is that .NET already has a deterministic tool for most questions you care about. The compiler, analyzers, unit tests, property tests, and BenchmarkDotNet each answer one question, and each answer is something an agent can read and act on.

#### The verification ladder

Think of these checks as a ladder. Each rung is cheaper than the one above it and catches a different kind of bug. A change climbs one rung at a time, and it only reaches a person once every tool below has said yes.

![](img_p2_1.png)

Costly

6. Human review You decide
5. Allocation budget BenchmarkDotNet
4. Properties hold CsCheck

Climb up

3. Tests pass xUnit or MSTest
2. Analyzers clean Code analysis
1. Compiles C# compiler

Cheap *Figure 1. The verification ladder. Cheap, fast checks sit at the bottom.*

-----

**Tip. Not all output deserves the same trust. Compiler errors and seeded tests are deterministic, so they**

can gate a merge. Timings are statistical and need a stated tolerance. An agent's explanation is judgment, so it informs you but never gates.

---

## Make the compiler do the work

The cheapest check is the one you already run on every build. If you model your domain so that an illegal state can't be written down, the compiler rejects the bug before a single test runs. People call this "make illegal states unrepresentable."

#### Put each operation on its legal state

Suppose a payment's status lives in a string, and a switch with a catch-all decides whether a refund is allowed. A new status falls into the catch-all and quietly becomes refundable. Instead, give each state its own type and put each method only where it's legal:

```
public abstract record Payment(Guid Id, long Cents);
public sealed record Authorized(Guid Id, long Cents)
: Payment(Id, Cents)
{
public Captured Capture() => new(Id, Cents, 0);
}
public sealed record Captured(Guid Id, long Cents, long Refunded)
: Payment(Id, Cents)
{
public Captured Refund(long cents) =>
this with { Refunded = Refunded + cents };
}
Authorized has no Refund method. If an agent tries to refund an uncaptured payment, the build fails with
CS1061, which means the type has no such member:
error CS1061: 'Authorized' does not contain a definition for 'Refund'
```

The error names the type and the missing member. An agent can read that line and fix the code without guessing.

#### Let nullable analysis find missing values

Turn on nullable reference types with <Nullable>enable</Nullable> in your project file. Now string means "never null" and string? means "might be null." The compiler warns when you dereference something that might be null (CS8602) or leave a non-nullable property unset (CS8618). Warnings are easy to ignore, so make them count. Set TreatWarningsAsErrors to true in the project, or

build with dotnet build -warnaserror. A warning is now a red build.

-----

**Note. Types have escape hatches. The ! operator, default, and a catch-all \_ arm all compile. Analyzers**

are the next rung up: they can turn those patterns into build errors too.

-----

## Gates and evidence

Each rung on the ladder becomes a gate: a check that passes or fails a change. When every gate runs from one command, the agent, you, and CI all see the same result. A green local run predicts a green pull request. Each gate also leaves evidence. The table shows what that evidence proves, and just as important, what it doesn't.

<table>
<tr>
<th>Gate</th>
<th>Tool</th>
<th>Evidence</th>
<th></th>
</tr>
<tr>
<th></th>
<th></th>
<th>What it proves</th>
<th>What it doesn 't</th>
</tr>
<tr>
<td>Build</td>
<td>C# compiler with nullable enabled</td>
<td>Types line up; illegal calls are rejected</td>
<td>That the arithmetic is right</td>
</tr>
<tr>
<td>Analyzers</td>
<td>.NET code analysis, - warnaserror</td>
<td>Known risky patterns are absent</td>
<td>That the logic meets the spec</td>
</tr>
<tr>
<td>Unit tests</td>
<td>xUnit or MSTest via dotnet test</td>
<td>The chosen examples behave</td>
<td>Anything about inputs nobody picked</td>
</tr>
<tr>
<td>Properties</td>
<td>CsCheck with a fixed seed</td>
<td>A rule holds across thousands of inputs</td>
<td>That the rule itself is the right one</td>
</tr>
<tr>
<td>Coverage</td>
<td>dotnet test --collect</td>
<td>Which lines ran during tests</td>
<td>That any test checked the result</td>
</tr>
<tr>
<td>Allocations</td>
<td>BenchmarkDotNet memory diagnoser</td>
<td>Bytes allocated per operation</td>
<td>Wall-clock speed on another machine</td>
</tr>
</table>

Notice the coverage row. A test that calls a method and throws away the result still counts every line as covered. Record coverage, but don't treat it as proof.

#### State the rule, not the example

An example test says "this input gives that output." A property says "for every input, this rule holds." A library such as CsCheck generates thousands of inputs, shrinks any failure to a small case, and prints a seed so you can replay it exactly. Say you split a refund across order lines. The rule is simple: the lines add up to the refund.

-----

```python
using CsCheck;
using Xunit;
public class SplitProperties
{
static readonly Gen<(long total, long[] weights)> Cases =
from total in Gen.Long[1, 10_000]
from weights in Gen.Long[1, 10].Array[1, 6]
select (total, weights);
```

```
[Fact]
public void Lines_add_up_to_the_refund() =>
Cases.Sample(c =>
Allocator.Split(c.total, c.weights).Sum() == c.total);
}
```

If each line is rounded on its own, this property finds an uneven split where a cent goes missing. Example tests that only use even splits never would.

---

**Tip. Properties are part of your spec. If an agent "fixes" a failing property by weakening it, that is a**

change to the rules, and a person should decide.

---

#### The agent loop

Put the gates together and you get a loop. The agent proposes a diff. The build and analyzers run, then the tests and properties. If any gate fails, its output goes straight back to the agent as feedback. Only a change that passes every gate reaches you, along with the evidence.

![](img_p6_1.png)

The agent loop

Reject: tool output goes back as feedback

| Agent | Build + | Tests + | Gate |
|---|---|---|---|
| proposes diff | analyzers | properties | decision |

Every gate gives a yes or no.

Only tool output can block a merge.

Accept

Human review reads the evidence, merges

*Figure 2. The agent loop. Failing output becomes the agent's next prompt.*

This loop works because every message in it is precise. CS1061 names the type. A failed property prints the seed and the smallest failing input. The agent doesn't need to guess what went wrong, and you don't need to read every line to trust what passed.

-----

## Where to go next

Start small. Pick one rung you don't use yet and add it to a single project.

Turn on nullable reference types and TreatWarningsAsErrors in one project file. [<u>Read Overview of</u>](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview) .NET source code analysis to enable and tune the built-in analyzers. [<u>Read Testing in</u>](https://learn.microsoft.com/dotnet/core/testing/) .NET for unit testing with xUnit, NUnit, or MSTest, and for collecting code coverage. Write one property for a method that does arithmetic, and keep the seed when it fails. For the full walkthrough, with a bug for every rung, see the Verifiable .NET guide.

---

**Remember. Agents propose. Tools decide. You own the rules and the merge button.**