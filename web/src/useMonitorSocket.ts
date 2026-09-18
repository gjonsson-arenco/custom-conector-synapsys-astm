import { useEffect, useRef, useState } from "react";

/**
 * Se suscribe a un WebSocket del monitor y mantiene una ventana con los ultimos mensajes.
 * Reconecta solo si se cae. Devuelve tambien si el socket esta vivo.
 */
export function useMonitorSocket<T>(path: string, max = 300) {
  const [items, setItems] = useState<T[]>([]);
  const [connected, setConnected] = useState(false);
  const closed = useRef(false);

  useEffect(() => {
    closed.current = false;
    let socket: WebSocket | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const url = `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}${path}`;

    const connect = () => {
      socket = new WebSocket(url);

      socket.onopen = () => setConnected(true);

      socket.onmessage = (event) => {
        try {
          const parsed = JSON.parse(event.data) as T;
          setItems((prev) => {
            const next = [...prev, parsed];
            return next.length > max ? next.slice(next.length - max) : next;
          });
        } catch {
          /* ignore malformed frame */
        }
      };

      socket.onclose = () => {
        setConnected(false);
        if (!closed.current) {
          retry = setTimeout(connect, 1500);
        }
      };

      socket.onerror = () => socket?.close();
    };

    connect();

    return () => {
      closed.current = true;
      if (retry) clearTimeout(retry);
      socket?.close();
    };
  }, [path, max]);

  const clear = () => setItems([]);

  return { items, connected, clear };
}
