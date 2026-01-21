# Mermaid ポインティングモード検証

## 1. Flowchart (基本)

```mermaid
flowchart LR
    A[Start] --> B{Decision}
    B -->|Yes| C[Action 1]
    B -->|No| D[Action 2]
    C --> E[End]
    D --> E
```

## 2. Flowchart (複雑)

```mermaid
flowchart TD
    subgraph Input
        A1[File] --> A2[Parse]
    end
    subgraph Process
        B1[Validate] --> B2[Transform]
        B2 --> B3[Output]
    end
    A2 --> B1
```

## 3. Sequence Diagram

```mermaid
sequenceDiagram
    participant U as User
    participant C as Claude
    participant M as MCP Server
    U->>C: Request
    C->>M: Tool Call
    M-->>C: Response
    C-->>U: Answer
```

## 4. Class Diagram

```mermaid
classDiagram
    class Animal {
        +String name
        +eat()
        +sleep()
    }
    class Dog {
        +bark()
    }
    class Cat {
        +meow()
    }
    Animal <|-- Dog
    Animal <|-- Cat
```

## 5. State Diagram

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Running : Start
    Running --> Paused : Pause
    Paused --> Running : Resume
    Running --> [*] : Stop
```

## 6. ER Diagram

```mermaid
erDiagram
    USER ||--o{ ORDER : places
    ORDER ||--|{ ITEM : contains
    USER {
        int id
        string name
    }
    ORDER {
        int id
        date created
    }
```

## 7. Gantt Chart

```mermaid
gantt
    title Project Schedule
    dateFormat  YYYY-MM-DD
    section Phase 1
    Task A :a1, 2024-01-01, 30d
    Task B :a2, after a1, 20d
    section Phase 2
    Task C :b1, after a2, 15d
```

## 8. Pie Chart

```mermaid
pie title Language Usage
    "PowerShell" : 40
    "C#" : 35
    "Python" : 15
    "Other" : 10
```

## 9. Git Graph

```mermaid
gitGraph
    commit id: "init"
    branch feature
    commit id: "feat-1"
    commit id: "feat-2"
    checkout main
    merge feature id: "merge"
    commit id: "release"
```

## 10. Mindmap

```mermaid
mindmap
    root((MarkdownViewer))
        Features
            Markdown
            Mermaid
            KaTeX
        MCP
            show_markdown
            get_tabs
        Module
            Show-MarkdownViewer
            Get-MarkdownViewerTab
```

## 11. 小さいノード

```mermaid
flowchart LR
    A[A] --> B[B]
    B --> C[C]
```

## 12. 長いテキスト

```mermaid
flowchart TD
    A[This is a very long node text that might cause issues] --> B[Another long text node for testing]
```
