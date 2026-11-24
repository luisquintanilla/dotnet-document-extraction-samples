<table>
<tr>
<th rowspan="2">Operation</th>
<th>Input shapes</th>
<th></th>
<th>Result shape</th>
</tr>
<tr>
<th></th>
<th>B</th>
<th></th>
</tr>
<tr>
<td>Element-wise add</td>
<td>2, 2</td>
<td>2, 2</td>
<td>2, 2</td>
</tr>
<tr>
<td>Broadcasting add</td>
<td>4, 3</td>
<td>1, 3</td>
<td>4, 3</td>
</tr>
<tr>
<td>Dot product</td>
<td>3</td>
<td>3</td>
<td>[] (a scalar)</td>
</tr>
<tr>
<td>Matrix multiply</td>
<td>2, 3</td>
<td>3, 2</td>
<td>2, 2</td>
</tr>
</table>

Broadcasting compares shapes from right to left. Two dimensions are compatible when they are equal or when one of them is 1. So [4, 3] and [1, 3] work, but [4, 3] and [4] do not. Matrix multiply

Matrix multiply is built from dot products. Each cell of the result is the dot product of one row of A with one column of B. For A with shape [2, 3] and B with shape [3, 2], the inner sizes (3 and 3) must match, and the result has shape [2, 2].

Matrix multiply: [2, 3] × [3, 2] = [2, 2]

A [2, 3] B [3, 2] C [2, 2]

Row 1 of A 2 64 • column 1 of B

10 1×7 + 2x9 + 3×11 5 6 139 154

= 7 + 18 + 33

11 12

= 58

Each cell of C is the dot product of a row of A and a column of B.

= [[58, 64], [139, 154]]

Figure 2. A worked [2, 3] × [3, 2] matrix multiply. Row 1 of A and column 1 of B give 58. You can check the other three cells yourself:

- Row 1, column 2: 1×8 + 2×10 + 3×12 = 8 + 20 + 36 = 64
- Row 2, column 1: 4×7 + 5x9 + 6x11 = 28 + 45 + 66 = 139
- Row 2, column 2: 4x8 + 5×10 + 6×12 = 32 + 50 + 72 = 154 Here is the same multiply in code. Storing B one column at a time makes every column a contiguous span, so each cell is a single call to TensorPrimitives.Dot.