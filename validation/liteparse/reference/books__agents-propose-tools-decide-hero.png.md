# Gates and evidence

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
<th>What it doesn't</th>
</tr>
<tr>
<td>Build</td>
<td>C# compiler with nullable enabled</td>
<td>Types line up; illegal calls are rejected</td>
<td>That the arithmetic is right</td>
</tr>
<tr>
<td>Analyzers</td>
<td>NET code analysis, - warnaserror</td>
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

## State the rule, not the example

An example test says "this input gives that output." A property says "for every input, this rule holds." A library such as CsCheck generates thousands of inputs, shrinks any failure to a small case, and prints a seed so you can replay it exactly.

Say you split a refund across order lines. The rule is simple: the lines add up to the refund.