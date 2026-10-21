import { ExecApi } from "./exec";
import { FilesApi } from "./files";
import { HttpApi } from "./http";
export interface ConnectOptions {
    url?: string;
    timeoutMs?: number;
}
export declare class AgentClient {
    private readonly connection;
    readonly exec: ExecApi;
    readonly fs: FilesApi;
    readonly http: HttpApi;
    private constructor();
    static connect(options?: ConnectOptions): Promise<AgentClient>;
    close(): void;
}
export declare function connect(options?: ConnectOptions): Promise<AgentClient>;
