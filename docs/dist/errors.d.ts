export type AgentErrorCode = "request.invalid" | "job.cancelled" | "job.timeout" | "job.failed" | "agent.internal";
export declare class AgentError extends Error {
    readonly code: AgentErrorCode | string;
    constructor(code: AgentErrorCode | string, message: string);
}
export declare class ConnectionClosedError extends Error {
    constructor(message?: string);
}
export declare class ProtocolError extends Error {
    constructor(message: string);
}
