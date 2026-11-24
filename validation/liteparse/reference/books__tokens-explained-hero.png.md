# Tokenizers in .NET

The Microsoft.ML.Tokenizers library brings popular model tokenizers to C# with no Python dependency.

Every tokenizer derives from the same Tokenizer base class, so encoding, decoding, and counting look the same for all of them.

<table>
<tr>
<th>Tokenizer</th>
<th>Algorithm</th>
<th>NET type</th>
<th colspan="2">Where you'll see it Model families Typical use</th>
</tr>
<tr>
<td>Tiktoken</td>
<td>Optimized BPE</td>
<td>TiktokenTokenizer</td>
<td>GPT-4o, GPT-4, o1, 03</td>
<td>Cost and context checks for OpenAI calls</td>
</tr>
<tr>
<td>BPE</td>
<td>Byte pair encoding</td>
<td>BpeTokenizer</td>
<td>GPT-2, RoBERTa</td>
<td>Custom vocab and merges files</td>
</tr>
<tr>
<td>SentencePiece (Llama)</td>
<td>SentencePiece (BPE or Unigram)</td>
<td>LlamaTokenizer</td>
<td>Llama 2, T5, mBART</td>
<td>Open-weight and multilingual models</td>
</tr>
<tr>
<td>WordPiece (BERT)</td>
<td>WordPiece</td>
<td>BertTokenizen</td>
<td>BERT, DistilBERT</td>
<td>Embeddings and classification</td>
</tr>
</table>

# The tokenization pipeline

| Input text | Normalizer | → Pre-tokenizer | Model | Token IDs |
|---|---|---|---|---|
| "Hello, world!" | optional | optional | core engine | 13225, 11, |
|  | cleans text | splits chunks | BPE, WordPiece | 2375, 0 |

Decode runs the other way: Token IDs back to text. Figure 2. Every encode call runs text through the same pipeline of stages.

using Microsoft.ML. Tokenizers;

TiktokenTokenizer tokenizer =

TiktokenTokenizer.CreateForModel("gpt-40");

int count = tokenizer.CountTokens ("Hello, world!");

Console.WriteLine($"Tokens: {count}"); // Tokens: 4