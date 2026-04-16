# AG-UI-Sales-Pipeline-Demo

This project demonstrates an **Agentic UI (AG-UI)** for managing a sales pipeline. It combines a Blazor-based client with an AI agent backend to provide an interactive, data-driven experience where an agent can reason about data and directly manipulate the user interface.

## Architecture & AG-UI Usage

The application leverages the **Microsoft.Agents.AI.AGUI** framework to facilitate communication between the user, the UI, and the AI agent.

### Client-Agent Communication
- **AguiClient**: Located in `SalesPipelineDemo.Client/Services`, this service wraps `AGUIChatClient`. it handles the streaming of chat responses and tool execution events from the backend.
- **SSE (Server-Sent Events)**: The agent communicates updates via SSE. This includes:
    - **TextContent**: Streaming narration from the agent.
    - **FunctionCallContent**: Logs of tools the agent is currently calling.
    - **DataContent (application/json)**: "State Snapshots" containing structured data for UI components.
- **SignalR**: Used for real-time broadcasts that affect all users, such as bulk operation previews and data updates.

### Tool-Driven UI
The agent has access to specific tools that allow it to "drive" the UI:
- `render_component`: Tells the client to display a specific visualization (grid, chart, etc.) for a subset of data.
- `bulk_operation`: Prepares a set of changes (e.g., assigning owners or updating stages) and presents them to the user as a preview before committing.
- `get_pipeline_stats`: Allows the agent to query the latest metrics to provide accurate narration.

## Components

The UI is built using several modular Blazor components:

### AgentChat
The primary interaction point. It features:
- **Streaming Narratives**: Real-time text responses from the AI.
- **Activity Log**: A collapsible log showing the agent's "thinking" process (LLM calls and tool executions).
- **Workflow Stepper**: Quick-action buttons that guide the user through a typical "Analyze -> Fix -> Summary" cycle.
- **Bulk Approval UI**: When a bulk operation is staged, a dedicated panel appears here for the user to approve or cancel the changes.

### DynamicCanvas
The main workspace on the right side of the screen. It acts as a container for agent-rendered components, managing "slots" that can be dynamically filled, updated, or removed by the agent.

### Data Visualization Components
- **DataGrid**: A powerful grid for viewing and editing opportunities. It supports:
    - **Inline Editing**: Double-click any cell to change its value.
    - **Preview Mode**: Highlights rows in yellow when a bulk operation is staged but not yet approved.
- **DataChart**: Visualizes pipeline data using charts (e.g., a Forecast Waterfall or a Data Quality Trend line chart).
- **DataGauge**: Displays a "Global Pipeline Confidence" score based on data quality and probability.
- **DataPanel**: A high-level summary panel showing key metrics like Total Forecast, Weighted Forecast, and Adjusted Forecast.

## Getting Started

1. **Configure the LLM**: Set your OpenAI-compatible API key and base URL in `SalesPipelineDemo.Api/appsettings.json`.
2. **Run the API**: Start the `SalesPipelineDemo.Api` project.
3. **Run the Client**: Start the `SalesPipelineDemo.Client` project.
4. **Interact**: Open the client in your browser and ask the agent to "Analyze my sales pipeline".