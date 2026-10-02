import { createTaskInteractionBroker, startSessionBridge } from './bridge-core.js'

export const name = 'halo-session-bridge'
export const inject = ['sessionController', 'workspaceController']

/**
 * Mount the HaloPixelToolBox session bridge inside the DSH Host. GET session
 * reads remain cold-safe; device commands use one explicit, serialized writer.
 */
export async function apply(ctx, config = {}) {
  const taskInteractions = createTaskInteractionBroker({
    agents: ctx.get?.('agents'),
    userQuestions: ctx.get?.('userQuestions'),
  })
  const disposeApproval = ctx.on(
    'approval/request',
    (request, next) => taskInteractions.handleApproval(request, next),
    { global: true, prepend: true },
  )
  const disposeQuestions = ctx.on(
    'user-questions/request',
    (request, next) => taskInteractions.handleQuestion(request, next),
    { global: true, prepend: true },
  )
  let bridge
  try {
    bridge = await startSessionBridge({
      sessionController: ctx.sessionController,
      workspaceController: ctx.workspaceController,
      taskInteractions,
      config,
      logger: ctx.logger,
    })
  } catch (error) {
    disposeQuestions()
    disposeApproval()
    taskInteractions.close()
    throw error
  }

  try {
    ctx.effect(
      () => async () => {
        disposeQuestions()
        disposeApproval()
        await bridge.close()
      },
      'halo-session-bridge.listen',
    )
  } catch (error) {
    disposeQuestions()
    disposeApproval()
    await bridge.close()
    throw error
  }
}
