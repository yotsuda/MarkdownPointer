# Edge case: empty lines before code

Some text.


```
after two empty lines
```

## Consecutive code blocks

```python
first block
```
```js
second block
```

## Nested quote with code

> quoted text
>
> ```
> code in quote
> ```
>
> more quoted text

## List with code

- item with code:
  ```
  code in list
  ```
- next item

## Table then code

| a | b |
|---|---|
| 1 | 2 |

```
after table
```

## Long gap

text at top



lots of empty lines above

```
code after gaps
```

## Mixed content

text1

> quote1

text2

- list1

text3

```
code1
```

text4

1. ordered1

text5
