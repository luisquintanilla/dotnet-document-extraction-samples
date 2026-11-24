# Tokens, Explained

A short introduction to tokenization in .NET

By Luis Quintanilla In this book

- What a token is, and why models read tokens instead of words •The tokenizer families you will meet in .NET, side by side
- How a tokenizer turns text into IDs, one stage at a time
- How to count tokens and keep your prompts inside a budget Where to go next to learn more

Before you start. All examples use the Microsoft.ML.Tokenizers NuGet package. The GPT-4o examples also need the Microsoft.ML. Tokenizers.Data.0200kBase data package.

-----

##### What is a token?

A language model never sees your text directly. Before the model runs, a tokenizer splits the text into small pieces called tokens, and then maps each piece to an integer called a token ID. The model works only with those numbers. When the model answers, the process runs in reverse. The model produces token IDs, and the tokenizer decodes them back into text you can read.

###### From text to tokens to IDs

<table>
<tr>
<th>1. Text</th>
<th></th>
<th colspan="4">"The cat sat on the mat"</th>
<th></th>
</tr>
<tr>
<th>2. Tokens</th>
<th>The</th>
<th>cat</th>
<th>Sat</th>
<th>on</th>
<th>the</th>
<th>mat</th>
</tr>
<tr>
<td>3. Token IDs</td>
<td>464</td>
<td>3857</td>
<td>3290</td>
<td>389</td>
<td>279</td>
<td>2450</td>
</tr>
</table>

The underscore (\_) marks a leading space that belongs to the token. Encode goes down this page. Decode goes back up. Illustrative only; real IDs depend on the tokenizer and its vocabulary.

Figure 1. Text becomes tokens, and tokens become IDs. The IDs are illustrative.

###### Why not just use words?

Splitting on words sounds simple, but English alone has hundreds of thousands of words, and rare words end up as "unknown." Splitting into single characters fixes that, but it makes every sequence very long.

Modern models use subword tokens instead. Common words stay whole, and rare words break into reusable pieces, such as "token" plus "ization." Vocabularies stay in the tens or hundreds of thousands of entries.

###### Three things to remember

•A token is not a word. A token often includes its leading space, so " world" is a single token.

- Every model has its own tokenizer. Use the one the model was trained with.
- Character counts are a poor guess. Always count tokens with the tokenizer.

-----

#### Tokenizers in .NET

The Microsoft.ML.Tokenizers library brings popular model tokenizers to C# with no Python dependency.

Every tokenizer derives from the same Tokenizer base class, so encoding, decoding, and counting look the same for all of them.

<table>
<tr>
<th>Tokenizer</th>
<th>Algorithm</th>
<th>LNET type</th>
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
<td>GPT-2, ROBERTa</td>
<td>Custom vocab and merges files</td>
</tr>
<tr>
<td>SentencePiece (Llama)</td>
<td>SentencePiece (BPE or LlamaTokenizer Unigram)</td>
<td></td>
<td>Llama 2, T5, mBART</td>
<td>Open-weight and multilingual models</td>
</tr>
<tr>
<td>WordPiece (BERT)</td>
<td>WordPiece</td>
<td>BertTokenizer</td>
<td>BERT, DistilBERT</td>
<td>Embeddings and classification</td>
</tr>
</table>

##### The tokenization pipeline

| Input text | Normalizer | → Pre-tokenizer | Model | Token IDs |
|---|---|---|---|---|
| "Hello, world!" | optional | optional | core engine | 13225, 11, |
|  | cleans text | splits chunks | BPE, WordPiece | 2375, 0 |

Decode runs the other way: Token IDs back to text. Figure 2. Every encode call runs text through the same pipeline of stages.

using Microsoft.ML.Tokenizers;

TiktokenTokenizer tokenizer =

TiktokenTokenizer.CreateForModel("gpt-4o");

int count = tokenizer.CountTokens("Hello, world!");

Console.WriteLine($"Tokens: {count)"); // Tokens: 4

-----

## Counting tokens and staying in budget

Every model has a context window, the maximum number of tokens it can read and write in one request.

GPT-4o, for example, has a 128K token window. You are also billed per token, so counting tokens helps you control both size and cost.

###### Cut text at a token limit

Cutting a string at 1,000 characters might give you 200 tokens or 800 tokens. Use GetIndexByTokenCount instead. It returns the character index where your token budget runs out, so you can slice the text safely.

int maxTokens = 500;

if (tokenizer.CountTokens(longText) > maxTokens)

int cut = tokenizer.GetIndexByTokenCount(

longText, maxTokens, out \_, out int used);

longText = longText[..cut];

Console.WriteLine($"Kept {used) tokens.");

To keep the most recent text instead, such as the end of a chat history, use GetIndexByTokenCountFromEnd and slice with text[cut.. ].

###### Plan your budget

Think of the context window as a budget you split between the parts of your prompt. The numbers below are only an example for a 128,000 token window.

| Part of the request | Example tokens |
|---|---|
| System prompt | 1,000 |
| Retrieved context | 100,000 |
| User question | 500 |
| Response reserve | 4,096 |
| Unused headroom | 22,404 |
| Total | 128,000 |

Tip. Always reserve tokens for the model's response. If your prompt fills the whole window, the model has no room left to answer. Also note that CountTokens counts only the text you pass in, not the extra formatting tokens a chat template adds.

### Where to go next

-----

You now know what tokens are, which tokenizer to pick, and how to stay inside a token budget. Here is where to keep learning:

The full NET tokenizers guide, with deep dives on each tokenizer, special tokens, and chunking for

RAG.

- Use tokenizers in .NET on Microsoft Learn, the official documentation for Microsoft.ML.Tokenizers.
- Try it yourself: count the tokens in your own prompts, then truncate one to fit a small budget.