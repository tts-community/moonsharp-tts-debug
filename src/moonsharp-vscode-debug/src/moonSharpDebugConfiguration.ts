'use strict'

import * as vscode from 'vscode'

export type PortProvidedCallback = (int) => void

export class MoonSharpDebugConfigurationProvider implements vscode.DebugConfigurationProvider {

	onPortProvided: PortProvidedCallback

	constructor(onPortProvided: PortProvidedCallback)
	{
		this.onPortProvided = onPortProvided
	}

	public provideDebugConfigurations(folder: vscode.WorkspaceFolder | undefined, token?: vscode.CancellationToken): vscode.ProviderResult<vscode.DebugConfiguration[]> {
		const path = folder ? folder.uri.fsPath : '${fileDirname}'
		return [
			{
				type: 'moonsharp-lua',
				name: 'Global',
				request: 'attach',
				path,
			}
		]
	}

	public async resolveDebugConfiguration?(folder: vscode.WorkspaceFolder | undefined, debugConfiguration: vscode.DebugConfiguration, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration | null> {
		const activeEditor = vscode.window.activeTextEditor

		if (!activeEditor || !activeEditor.document.languageId.endsWith('lua') || (debugConfiguration.type && debugConfiguration.type !== 'moonsharp-lua')) {
			return debugConfiguration
		}

		if (!debugConfiguration.debugServer) {
			const inputPort = await vscode.window.showInputBox({
				value: '41912',
				prompt: 'MoonSharp server port',
				placeHolder: 'Port',
				validateInput: (value: string) => {
					const port = parseInt(value)
					return (port > 0 && port <= 65535) ? null : 'Not a valid port number'
				},
				ignoreFocusOut: true,
			})

			if (!inputPort) {
				return null
			}

			debugConfiguration.debugServer = parseInt(inputPort)
		}

		if (!debugConfiguration.slave && this.onPortProvided) {
			this.onPortProvided(debugConfiguration.debugServer)
		}

		return {
			name: 'Global',
			request: 'attach',
			mode: 'directory',
			pipeline: [],
			path: folder ? folder.uri.fsPath : (activeEditor ? activeEditor.document.uri.fsPath : null),
			...debugConfiguration,
			type: 'moonsharp-lua',
		}
	}
}
