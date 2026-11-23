# Provider comparison: `tokens-explained-hero.pdf`

| Provider | Time | Headings | Text blocks | Tables | Images |
| --- | --- | --- | --- | --- | --- |
| PdfPig (local, text layer) | 0.4s | 1 | 34 | 0 | 0 |
| OCRia-nano-1 (ONNX, local) | 1.2s | 1 | 8 | 1 | 0 |
| Vision language model (Ollama) | 26.9s | 0 | 1 | 1 | 1 |
| Mistral Document AI | 10.2s | 0 | 2 | 0 | 1 |
| Azure Document Intelligence | 13.0s | 2 | 9 | 1 | 1 |
| Azure Content Understanding | 10.4s | 1 | 10 | 1 | 1 |

## PdfPig (local, text layer)

Headings found:

- Tokenizers in .NET

## OCRia-nano-1 (ONNX, local)

Headings found:

- Tokenizers in .NET

Table, 6 rows x 5 columns:

| Tokenizer | Algorithm | .NET type | Where you | 'll see it |
| --- | --- | --- | --- | --- |
|  |  |  | Model families | Typical use |
| Tiktoken | Optimized BPE | TiktokenTokenizer | GPT-4o, GPT-4, o1, o3 | Cost and context checks for OpenAI calls |
| BPE | Byte pair encoding | BpeTokenizer | GPT-2, RoBERTa | Custom vocab and merges files |
| SentencePiece (Llama) | SentencePiece (BPE or Unigram) | LlamaTokenizer | Llama 2, T5, mBART | Open-weight and multilingual models |
| WordPiece (BERT) | WordPiece | BertTokenizer | BERT, DistilBERT | Embeddings and classification |

## Vision language model (Ollama)

Headings found:

- (none)

Table, 6 rows x 5 columns:

| Tokenizer | Algorithm | .NET type | Where you'll see it |  |
| --- | --- | --- | --- | --- |
|  |  |  | Model families | Typical use |
| Tiktoken | Optimized BPE | TiktokenTokenizer | GPT-4o, GPT-4, o1, o3 | Cost and context checks for OpenAI calls |
| BPE | Byte pair encoding | BpeTokenizer | GPT-2, RoBERTa | Custom vocab and merges files |
| SentencePiece (Llama) | SentencePiece (BPE or Unigram) | LlamaTokenizer | Llama 2, T5, mBART | Open-weight and multilingual models |
| WordPiece (BERT) | WordPiece | BertTokenizer | BERT, DistilBERT | Embeddings and classification |

Image: The tokenization pipeline  ![Input text](https://example.com/input-text.png)  "Hello, world!"  optional cleans text  optional splits chunks  core engine BPE,...

## Mistral Document AI

Headings found:

- (none)

Image: (no description)

## Azure Document Intelligence

Headings found:

- Tokenizers in .NET
- The tokenization pipeline

Table, 6 rows x 5 columns:

| Tokenizer (spans 2 rows) | Algorithm (spans 2 rows) | .NET type (spans 2 rows) | Where you'll see it (spans 2 cols) |  |
| --- | --- | --- | --- | --- |
|  |  |  | Model families | Typical use |
| Tiktoken | Optimized BPE | TiktokenTokenizer | GPT-4o, GPT-4, o1, o3 | Cost and context checks for OpenAI calls |
| BPE | Byte pair encoding | BpeTokenizer | GPT-2, RoBERTa | Custom vocab and merges files |
| SentencePiece (Llama) | SentencePiece (BPE or Unigram) | LlamaTokenizer | Llama 2, T5, mBART | Open-weight and multilingual models |
| WordPiece (BERT) | WordPiece | BertTokenizer | BERT, DistilBERT | Embeddings and classification |

Image: Figure 2. Every encode call runs text through the same pipeline of stages.

## Azure Content Understanding

Headings found:

- Tokenizers in .NET

Table, 6 rows x 5 columns:

| Tokenizer (spans 2 rows) | Algorithm (spans 2 rows) | .NET type (spans 2 rows) | Where you'll see it (spans 2 cols) |  |
| --- | --- | --- | --- | --- |
|  |  |  | Model families | Typical use |
| Tiktoken | Optimized BPE | TiktokenTokenizer | GPT-4o, GPT-4, o1, o3 | Cost and context checks for OpenAI calls |
| BPE | Byte pair encoding | BpeTokenizer | GPT-2, RoBERTa | Custom vocab and merges files |
| SentencePiece (Llama) | SentencePiece (BPE or Unigram) | LlamaTokenizer | Llama 2, T5, mBART | Open-weight and multilingual models |
| WordPiece (BERT) | WordPiece | BertTokenizer | BERT, DistilBERT | Embeddings and classification |

Image: The tokenization pipeline

