# Tokenizers in .NET

The Microsoft.ML.Tokenizers library brings popular model tokenizers to C# with no Python dependency. Every tokenizer derives from the same Tokenizer base class, so encoding, decoding, and counting look the same for all of them.

| Tokenizer | Algorithm | .NET type | Where you Model families | 'll see it Typical use |
|---|---|---|---|---|
| Tiktoken | Optimized BPE | TiktokenTokenizer | GPT-4o, GPT-4, o1, o3 | Cost and context checks for OpenAI calls |
| BPE | Byte pair encoding | BpeTokenizer | GPT-2, RoBERTa | Custom vocab and merges files |
| SentencePiece (Llama) | SentencePiece (BPE or Unigram) | LlamaTokenizer | Llama 2, T5, mBART | Open-weight and multilingual models |
| WordPiece (BERT) | WordPiece | BertTokenizer | BERT, DistilBERT | Embeddings and classification |

![](img_p1_1.png)

The tokenization pipeline

Input text Normalizer Pre-tokenizer Model Token IDs

"Hello, world!" optional optional core engine 13225, 11, cleans text splits chunks BPE, WordPiece 2375, 0 Decode runs the other way: Token IDs back to text. *Figure 2. Every encode call runs text through the same pipeline of stages.*

```
using Microsoft.ML.Tokenizers;
TiktokenTokenizer tokenizer =
TiktokenTokenizer.CreateForModel("gpt-4o");
int count = tokenizer.CountTokens("Hello, world!");
Console.WriteLine($"Tokens: {count}"); // Tokens: 4
```